// PhotoShelf HEIC preview worker. Input and output are pipes; no file paths are
// accepted. The caller owns the read-only source handle, process timeout and job.
#include <libheif/heif.h>
#include <libde265/de265.h>

#include <algorithm>
#include <array>
#include <charconv>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <iostream>
#include <limits>
#include <memory>
#include <stdexcept>
#include <string_view>
#include <vector>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <fcntl.h>
#include <io.h>
#endif

namespace {
constexpr std::size_t MiB = 1024u * 1024u;
constexpr std::size_t MaxInputBytes = 128u * MiB;
constexpr std::uint64_t MaxSourcePixels = 64'000'000;
constexpr std::uint64_t MaxNativeMemory = 768u * MiB;

struct DecodeFailure : std::runtime_error {
  explicit DecodeFailure(const char* code) : std::runtime_error(code) {}
};

void require(bool condition, const char* code) {
  if (!condition) throw DecodeFailure(code);
}

void check(heif_error error) {
  // Native error strings can contain input-derived text. Only a fixed code is
  // exposed to the parent; no paths, metadata or unconstrained stderr output.
  require(error.code == heif_error_Ok, "heif_decode_failed");
}

struct Library {
  Library() { check(heif_init(nullptr)); }
  ~Library() { heif_deinit(); }
  Library(const Library&) = delete;
  Library& operator=(const Library&) = delete;
};

using Context = std::unique_ptr<heif_context, decltype(&heif_context_free)>;
using Handle = std::unique_ptr<heif_image_handle, decltype(&heif_image_handle_release)>;
using Image = std::unique_ptr<heif_image, decltype(&heif_image_release)>;
using Options = std::unique_ptr<heif_decoding_options, decltype(&heif_decoding_options_free)>;

int parse_dimension(int argc, char** argv) {
  require(argc == 3 && std::string_view(argv[1]) == "--max-dimension", "invalid_arguments");
  std::string_view argument(argv[2]);
  int dimension = 0;
  auto result = std::from_chars(argument.data(), argument.data() + argument.size(), dimension);
  require(result.ec == std::errc{} && result.ptr == argument.data() + argument.size()
          && dimension >= 32 && dimension <= 4096, "invalid_dimension");
  return dimension;
}

std::vector<std::uint8_t> read_input() {
  std::vector<std::uint8_t> input;
  std::array<char, 64u * 1024u> chunk{};
  while (std::cin) {
    std::cin.read(chunk.data(), static_cast<std::streamsize>(chunk.size()));
    const auto count = static_cast<std::size_t>(std::cin.gcount());
    require(count <= MaxInputBytes - input.size(), "input_too_large");
    const auto* first = reinterpret_cast<const std::uint8_t*>(chunk.data());
    input.insert(input.end(), first, first + count);
  }
  require(std::cin.eof() && !std::cin.bad(), "input_read_failed");
  require(input.size() >= 12 && std::memcmp(input.data() + 4, "ftyp", 4) == 0, "invalid_heif_header");
  return input;
}

void set_limits(heif_context* context) {
  // Assign every version-4 field, independent of environment/defaults. A zero
  // field disables its limit in libheif, so all public limits are nonzero.
  auto* limits = heif_context_get_security_limits(context);
  require(limits != nullptr && limits->version >= 4, "unsupported_native_version");
  limits->max_image_size_pixels = MaxSourcePixels;
  limits->max_number_of_tiles = 1024;
  limits->max_bayer_pattern_pixels = 16;
  limits->max_items = 4096;
  limits->max_color_profile_size = 4u * MiB;
  limits->max_memory_block_size = 256u * MiB;
  limits->max_components = 4;
  limits->max_iloc_extents_per_item = 1024;
  limits->max_size_entity_group = 1024;
  limits->max_children_per_box = 4096;
  limits->max_total_memory = MaxNativeMemory;
  limits->max_sample_description_box_entries = 32;
  limits->max_sample_group_description_box_entries = 256;
  limits->max_sequence_frames = 256;
  limits->max_number_of_file_brands = 32;
  limits->max_bad_pixels = 1024;
  limits->max_iso23001_17_pixel_size_bytes = 16;
  limits->parent = nullptr;
  heif_context_set_max_decoding_threads(context, 2);
}

void validate_dimensions(int width, int height) {
  require(width > 0 && height > 0
          && static_cast<std::uint64_t>(width) * static_cast<std::uint64_t>(height) <= MaxSourcePixels,
          "source_dimensions_exceeded");
}

void write_u32(std::uint32_t value) {
  std::array<char, 4> bytes{};
  for (unsigned index = 0; index < 4; ++index)
    bytes[index] = static_cast<char>((value >> (index * 8u)) & 0xffu);
  std::cout.write(bytes.data(), static_cast<std::streamsize>(bytes.size()));
}

void decode(const std::vector<std::uint8_t>& input, int maximum_dimension) {
  Library library;
  require(heif_have_decoder_for_format(heif_compression_HEVC) != 0, "hevc_decoder_unavailable");
  Context context(heif_context_alloc(), heif_context_free);
  require(context != nullptr, "allocation_failed");
  set_limits(context.get());
  check(heif_context_read_from_memory_without_copy(context.get(), input.data(), input.size(), nullptr));

  heif_image_handle* raw_handle = nullptr;
  check(heif_context_get_primary_image_handle(context.get(), &raw_handle));
  Handle handle(raw_handle, heif_image_handle_release);
  require(handle != nullptr, "primary_image_missing");
  validate_dimensions(heif_image_handle_get_width(handle.get()), heif_image_handle_get_height(handle.get()));

  Options options(heif_decoding_options_alloc(), heif_decoding_options_free);
  require(options != nullptr && options->version >= 10, "unsupported_native_version");
  options->ignore_transformations = false;
  options->convert_hdr_to_8bit = true;
  options->strict_decoding = true;
  options->decoder_id = "libde265";
  options->num_library_threads = 2;
  options->num_codec_threads = 2;
  options->output_image_nclx_profile = nullptr;
  options->output_image_nclx_profile_passthrough = false;

  heif_image* raw_image = nullptr;
  check(heif_decode_image(handle.get(), &raw_image, heif_colorspace_RGB,
                          heif_chroma_interleaved_RGBA, options.get()));
  Image image(raw_image, heif_image_release);
  require(image != nullptr, "decoded_image_missing");
  int width = heif_image_get_width(image.get(), heif_channel_interleaved);
  int height = heif_image_get_height(image.get(), heif_channel_interleaved);
  validate_dimensions(width, height);

  const int longest = std::max(width, height);
  if (longest > maximum_dimension) {
    width = std::max(1, static_cast<int>(static_cast<std::int64_t>(width) * maximum_dimension / longest));
    height = std::max(1, static_cast<int>(static_cast<std::int64_t>(height) * maximum_dimension / longest));
    heif_image* scaled = nullptr;
    check(heif_image_scale_image(image.get(), &scaled, width, height, nullptr));
    image.reset(scaled);
    require(image != nullptr, "scaled_image_missing");
  }

  // Bounds come from the actual decoded channel, not potentially inconsistent
  // container dimensions. libheif's rows may include padding.
  width = heif_image_get_width(image.get(), heif_channel_interleaved);
  height = heif_image_get_height(image.get(), heif_channel_interleaved);
  require(width > 0 && height > 0 && width <= maximum_dimension && height <= maximum_dimension,
          "output_dimensions_exceeded");
  require(heif_image_get_chroma_format(image.get()) == heif_chroma_interleaved_RGBA
          && heif_image_get_bits_per_pixel_range(image.get(), heif_channel_interleaved) == 8,
          "unexpected_pixel_format");
  std::size_t native_stride = 0;
  const auto* pixels = heif_image_get_plane_readonly2(image.get(), heif_channel_interleaved, &native_stride);
  const auto output_stride = static_cast<std::size_t>(width) * 4u;
  require(pixels != nullptr && native_stride >= output_stride
          && native_stride <= MaxNativeMemory / static_cast<std::size_t>(height), "invalid_pixel_plane");
  const bool premultiplied = heif_image_is_premultiplied_alpha(image.get()) != 0;
  std::vector<std::uint8_t> row(output_stride);

  std::cout.write("PSH1", 4);
  write_u32(static_cast<std::uint32_t>(width));
  write_u32(static_cast<std::uint32_t>(height));
  write_u32(static_cast<std::uint32_t>(output_stride));
  for (int y = 0; y < height; ++y) {
    const auto* source = pixels + static_cast<std::size_t>(y) * native_stride;
    if (premultiplied) {
      std::copy_n(source, output_stride, row.data());
      for (std::size_t x = 0; x < output_stride; x += 4) {
        const unsigned alpha = row[x + 3];
        for (std::size_t channel = 0; channel < 3; ++channel)
          row[x + channel] = alpha == 0 ? 0 : static_cast<std::uint8_t>(
            std::min(255u, (static_cast<unsigned>(row[x + channel]) * 255u + alpha / 2u) / alpha));
      }
      source = row.data();
    }
    std::cout.write(reinterpret_cast<const char*>(source), static_cast<std::streamsize>(output_stride));
    require(std::cout.good(), "output_write_failed");
  }
  std::cout.flush();
  require(std::cout.good(), "output_write_failed");
}
} // namespace

int main(int argc, char** argv) {
#ifdef _WIN32
  SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
  if (_setmode(_fileno(stdin), _O_BINARY) == -1 || _setmode(_fileno(stdout), _O_BINARY) == -1) {
    std::fputs("pipe_initialization_failed\n", stderr);
    return 2;
  }
  _putenv_s("LIBHEIF_SECURITY_LIMITS", "");
#else
  unsetenv("LIBHEIF_SECURITY_LIMITS");
#endif
  try {
    if (argc == 2 && std::string_view(argv[1]) == "--version") {
      std::cout << "PhotoShelf.HeifWorker PSH1; libheif " << heif_get_version()
                << "; libde265 " << de265_get_version() << '\n';
      return 0;
    }
    const int dimension = parse_dimension(argc, argv);
    // Block on the input pipe before parsing media. The parent attaches the job
    // and its hard memory limit before sending even the first input byte.
    const auto input = read_input();
    decode(input, dimension);
    return 0;
  } catch (const DecodeFailure& error) {
    std::fputs(error.what(), stderr);
    std::fputc('\n', stderr);
  } catch (const std::bad_alloc&) {
    std::fputs("allocation_failed\n", stderr);
  } catch (...) {
    std::fputs("heif_worker_failed\n", stderr);
  }
  return 2;
}

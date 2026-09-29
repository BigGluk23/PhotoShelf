using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PhotoShelf.Application.Media;

public sealed record HeifInstallationInfo(string Protocol, string LibheifVersion, string Libde265Version);

public sealed class HeifInstallationException : IOException
{
    public HeifInstallationException(string detail, Exception? inner = null)
        : base("Комплект HEIC-декодера PhotoShelf недоступен или несовместим. " + detail +
               " Закройте PhotoShelf и распакуйте весь ZIP в новую папку, сохранив codecs/heif рядом с EXE. " +
               "Оригиналы фотографий изменять или восстанавливать не нужно.", inner) { }
}

public sealed partial class HeifDecoderClient
{
    private void VerifyInstallationFiles()
    {
        if (!File.Exists(_executablePath))
            throw new HeifInstallationException("Не найден запускаемый компонент: " + Path.GetFileName(_executablePath) + ".");
        if (!_requireBundledLibraries) return; // An explicitly injected probe/launcher may not use native DLLs.
        var directory = Path.GetDirectoryName(Path.GetFullPath(_executablePath))!;
        foreach (var name in new[] { Path.GetFileName(_executablePath), "heif.dll", "libde265.dll" })
        {
            try
            {
                using var file = new FileStream(Path.Combine(directory, name), FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.Length == 0) throw new IOException("Empty decoder component.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { throw new HeifInstallationException("Отсутствует, пуст или недоступен файл " + name + ".", exception); }
        }
    }

    /// <summary>
    /// Read-only capability check, with the same concurrency/job/termination bounds as decoding.
    /// Call from a background worker: opening an installation on a network drive can be synchronous.
    /// Compatible trusted LGPL replacements are accepted; installed DLL hashes are not an admission rule.
    /// </summary>
    public async Task<HeifInstallationInfo> VerifyInstallationAsync(CancellationToken token = default)
    {
        await DecoderSlot.WaitAsync(token).ConfigureAwait(false);
        Process? process = null; WindowsHeifJob? job = null;
        var started = false; var cleanupTransferred = false;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var tasks = new List<Task>(3);
        try
        {
            VerifyInstallationFiles();
            token.ThrowIfCancellationRequested();
            lifetime.CancelAfter(_timeout);
            process = new Process { StartInfo = CreateStartInfo(["--version"]) };
            if (!process.Start()) throw new IOException("Could not start the HEIF worker.");
            started = true;
            if (OperatingSystem.IsWindows() && !process.WaitForExit(0))
            {
                try { job = WindowsHeifJob.Attach(process); }
                catch (System.ComponentModel.Win32Exception) when (process.WaitForExit(0))
                { /* --version may have already exited; no live process remains to contain. */ }
            }
            // No media is supplied. EOF also lets a test worker observe that its job is already attached.
            try { process.StandardInput.Close(); }
            catch (IOException) when (process.WaitForExit(0)) { /* Healthy fast --version already closed its pipe. */ }
            var output = ReadVersionAsync(process.StandardOutput.BaseStream, lifetime.Token);
            var errors = DrainErrorsAsync(process.StandardError.BaseStream, lifetime.Token);
            var exit = WaitForActualExitAsync(process, lifetime.Token);
            tasks.AddRange([output, errors, exit]);
            var pending = new List<Task>(tasks);
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).WaitAsync(lifetime.Token).ConfigureAwait(false);
                await completed.ConfigureAwait(false); pending.Remove(completed);
                if (ReferenceEquals(completed, exit) && exit.Result != 0)
                    throw new IOException($"HEIF worker exited with code {exit.Result}.");
            }
            return ParseInstallationVersion(await output.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && lifetime.IsCancellationRequested)
        { throw new HeifInstallationException("Проверка декодера превысила допустимое время."); }
        catch (Exception exception) when (exception is not (OperationCanceledException or HeifInstallationException))
        { throw new HeifInstallationException("Не удалось проверить совместимость декодера.", exception); }
        finally
        {
            lifetime.Cancel();
            if (started && process is not null)
            {
                if (!await TerminateAndWaitAsync(process, job).ConfigureAwait(false))
                {
                    cleanupTransferred = true;
                    _ = ReapAsync(process, job, null, tasks);
                }
                else await ObserveAsync(tasks).ConfigureAwait(false);
            }
            if (!cleanupTransferred) { job?.Dispose(); process?.Dispose(); DecoderSlot.Release(); }
            if (cleanupTransferred)
                throw new HeifInstallationException("Завершение процесса декодера ещё не подтверждено; повторный запуск заблокирован до его выхода.");
        }
    }

    private static async Task<string> ReadVersionAsync(Stream stream, CancellationToken token)
    {
        var bytes = new byte[1025]; var count = 0;
        while (true)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), token).ConfigureAwait(false);
            if (read == 0) return new UTF8Encoding(false, true).GetString(bytes, 0, count);
            count += read;
            if (count > 1024) throw new InvalidDataException("HEIF version response exceeds 1024 bytes.");
        }
    }

    private static HeifInstallationInfo ParseInstallationVersion(string text)
    {
        var match = Regex.Match(text.Trim(),
            @"\APhotoShelf\.HeifWorker (PSH1); libheif (\d+\.\d+\.\d+(?:[-+][A-Za-z0-9._-]+)?); libde265 (\d+\.\d+\.\d+(?:[-+][A-Za-z0-9._-]+)?)\z",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!match.Success) throw new HeifInstallationException("Декодер не подтвердил протокол PSH1 и версии библиотек.");
        static Version Numeric(string value) => Version.Parse(value.Split('-', '+')[0]);
        if (Numeric(match.Groups[2].Value) < new Version(1, 23, 5) || Numeric(match.Groups[3].Value) < new Version(1, 1, 3))
            throw new HeifInstallationException("Версии библиотек старее поддерживаемых libheif 1.23.5 / libde265 1.1.3.");
        return new(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value);
    }
}

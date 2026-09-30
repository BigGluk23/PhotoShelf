using System.Reflection;
using System.Security.Cryptography;
using PhotoShelf.Application.Updates;
using PhotoShelf.Application.Updates.Installation;

namespace PhotoShelf.Updater;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var selfTest = args.Length == 1 && args[0] == "--self-test";
        try
        {
            var command = UpdateHelperArguments.Parse(args);
            var currentVersion = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion.Split('+')[0] ?? throw new InvalidOperationException("The helper version is missing.");
            if (!UpdateVersion.TryParse(currentVersion, out _)) throw new InvalidOperationException("Invalid helper version.");
            if (command.Command == UpdateHelperCommand.SelfTest)
            {
                // No startup discovery, windows, directories, catalog, downloads or installation in this mode.
                using var rsa = RSA.Create(); rsa.ImportFromPem(UpdateTrust.PublicKeyPem);
                if (rsa.KeySize < 2048 || UpdateTrust.CurrentCatalogSchema != 5) return 2;
                Console.WriteLine("PhotoShelf updater self-test passed.");
                return 0;
            }
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Installation is supported only on Windows.");
            var paths = new UpdateInstallationPaths();
            if (command.Command == UpdateHelperCommand.Launch)
            {
                var active = ActiveInstallationResolver.Resolve(paths, UpdateTrust.PublicKeyPem, UpdateTrust.CurrentCatalogSchema)
                    ?? throw new InvalidOperationException("There is no activated PhotoShelf installation.");
                new UpdateProcessHost().Launch(new(active.ExecutablePath));
                return 0;
            }
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            using var window = new InstallationWindow(paths, currentVersion, command.RequestPath!);
            System.Windows.Forms.Application.Run(window);
            return window.ExitCode;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("PhotoShelf update stopped: " + error.Message);
            if (!selfTest && OperatingSystem.IsWindows())
                MessageBox.Show("Не удалось продолжить обновление PhotoShelf. Прежняя программа, каталог и оригиналы сохранены.\n\n" +
                    error.Message, "Обновление PhotoShelf", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
    }
}

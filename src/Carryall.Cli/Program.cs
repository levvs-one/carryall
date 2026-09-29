using System.Collections.Immutable;
using System.Globalization;
using Handinpack.Core;

return await CarryallCli.RunAsync(args).ConfigureAwait(false);

internal static class CarryallCli
{
    private const string Version = "0.2.0";

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        if (args[0] is "-v" or "--version")
        {
            Console.WriteLine(Version);
            return 0;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "build" => await BuildAsync(args[1..], cancellation.Token).ConfigureAwait(false),
                "verify" => await VerifyAsync(args[1..], cancellation.Token).ConfigureAwait(false),
                _ => Fail($"Неизвестная команда: {args[0]}")
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Операция отменена.");
            return 130;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
            UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine("Ошибка: " + exception.Message);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<int> BuildAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length < 2)
            return Fail("build требует путь результата и хотя бы один источник.");

        string output = Path.GetFullPath(args[0]);
        PackageFormat? requestedFormat = null;
        var sources = new List<string>();
        var extensions = ImmutableArray.CreateBuilder<string>();
        var required = ImmutableArray.CreateBuilder<string>();
        long? maxFile = null;
        long? maxTotal = null;
        long? maxOutput = null;

        for (int index = 1; index < args.Length; index++)
        {
            string token = args[index];
            switch (token)
            {
                case "--format":
                    requestedFormat = ParseFormat(NextValue(args, ref index, token));
                    break;
                case "--allow-ext":
                    foreach (string extension in NextValue(args, ref index, token)
                        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        extensions.Add(extension.StartsWith('.') ? extension : "." + extension);
                    }
                    break;
                case "--require":
                    required.Add(NextValue(args, ref index, token));
                    break;
                case "--max-file-mib":
                    maxFile = ParseMebibytes(NextValue(args, ref index, token), token);
                    break;
                case "--max-total-mib":
                    maxTotal = ParseMebibytes(NextValue(args, ref index, token), token);
                    break;
                case "--max-output-mib":
                    maxOutput = ParseMebibytes(NextValue(args, ref index, token), token);
                    break;
                default:
                    if (token.StartsWith("--", StringComparison.Ordinal))
                        return Fail("Неизвестная опция build: " + token);
                    sources.Add(token);
                    break;
            }
        }

        if (sources.Count == 0)
            return Fail("Не указан ни один источник.");

        PackageFormat format = requestedFormat ??
            (output.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? PackageFormat.Zip : PackageFormat.Folder);
        var rules = new PackageRules(extensions.ToImmutable(), maxFile, maxTotal, maxOutput, required.ToImmutable());

        Console.Error.WriteLine($"Сканирование: {sources.Count} источник(а/ов)...");
        PackagePlan plan = await PackagePlanner.ScanAsync(sources, rules, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!plan.CanBuild)
        {
            WriteIssues(plan.Issues);
            return 2;
        }

        Console.Error.WriteLine($"Сборка: {plan.Items.Count(item => item.Included)} файл(а/ов), {FormatBytes(plan.TotalBytes)}...");
        PackageResult result = await PackageWriter.WriteAsync(plan, output, format, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine(result.OutputPath);
        Console.Error.WriteLine(
            $"Готово и проверено: {result.FileCount} файл(а/ов), {FormatBytes(result.OutputBytes)}.");
        return 0;
    }

    private static async Task<int> VerifyAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length is < 1 or > 3)
            return Fail("verify: carryall verify <путь> [--format zip|folder]");

        string path = Path.GetFullPath(args[0]);
        PackageFormat? requestedFormat = null;
        if (args.Length > 1)
        {
            if (args.Length != 3 || args[1] != "--format")
                return Fail("verify: carryall verify <путь> [--format zip|folder]");
            requestedFormat = ParseFormat(args[2]);
        }

        PackageFormat format = requestedFormat ??
            (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? PackageFormat.Zip
                : PackageFormat.Folder);

        VerificationResult result = await PackageVerifier.VerifyAsync(path, format, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsValid)
        {
            WriteIssues(result.Issues);
            return 3;
        }

        Console.WriteLine($"OK\t{result.FileCount}\t{result.TotalBytes}\t{path}");
        return 0;
    }

    private static PackageFormat ParseFormat(string value) => value.ToLowerInvariant() switch
    {
        "zip" => PackageFormat.Zip,
        "folder" or "dir" or "directory" => PackageFormat.Folder,
        _ => throw new ArgumentException("Формат должен быть zip или folder.")
    };

    private static string NextValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length)
            throw new ArgumentException($"После {option} нужно значение.");
        return args[index];
    }

    private static long ParseMebibytes(string value, string option)
    {
        string normalized = value.Replace(',', '.');
        if (!decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                out decimal mib) ||
            mib <= 0 || mib > long.MaxValue / 1_048_576m)
        {
            throw new ArgumentException($"{option}: требуется положительное число МиБ.");
        }

        long bytes = decimal.ToInt64(decimal.Floor(mib * 1_048_576m));
        if (bytes <= 0)
            throw new ArgumentException($"{option}: значение слишком мало.");
        return bytes;
    }

    private static void WriteIssues(IEnumerable<PackageIssue> issues)
    {
        foreach (PackageIssue issue in issues.Take(100))
            Console.Error.WriteLine($"{issue.Code}: {issue.Message}" +
                                    (issue.Path is null ? string.Empty : $" [{issue.Path}]"));
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine("Используйте carryall --help.");
        return 64;
    }

    private static string FormatBytes(long value) => value switch
    {
        < 1024 => $"{value:N0} Б",
        < 1_048_576 => $"{value / 1024d:N1} КиБ",
        < 1_073_741_824 => $"{value / 1_048_576d:N1} МиБ",
        _ => $"{value / 1_073_741_824d:N2} ГиБ"
    };

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Carryall CLI 0.2.0
            Подготовка и проверка комплектов файлов без изменения оригиналов.

            Использование:
              carryall build <результат> <источник> [источник...] [опции]
              carryall verify <комплект> [--format zip|folder]
              carryall --version

            Опции build:
              --format zip|folder       Формат результата. Без опции определяется по .zip.
              --allow-ext .pdf,.dwg     Разрешённые расширения. Опцию можно повторять.
              --require <путь>          Обязательный относительный путь. Можно повторять.
              --max-file-mib <число>    Максимальный размер одного файла.
              --max-total-mib <число>   Максимальный размер материалов.
              --max-output-mib <число>  Максимальный размер готового результата.

            Примеры:
              carryall build handover.zip drawings specs.pdf --format zip
              carryall verify handover.zip
              carryall build out-folder project --format folder --allow-ext .pdf,.dwg
            """);
    }
}

using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Handinpack.Core;
using Microsoft.Win32;

namespace Handinpack.App;

public partial class MainWindow : Window
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly JsonSerializerOptions RecipeJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 24
    };

    private PackagePlan plan = new([], [], new(AllowedExtensions: [], RequiredPaths: []), [], [], 0);
    private readonly List<ImmutableArray<PackageItem>> edits = new();
    private CancellationTokenSource? operation;
    private string? lastOutput;
    private bool initialized;
    private bool refreshing;
    private string? ruleError;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var area = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, area.Width);
        MinHeight = Math.Min(MinHeight, area.Height);
        Width = Math.Min(Width, area.Width);
        Height = Math.Min(Height, area.Height);
        initialized = true;
        RefreshPlan();
        StatusText.Text = "Добавьте файлы или папки. Оригиналы останутся на месте.";
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SourcesColumn is null || SourcesPanel is null)
            return;

        bool narrow = ActualWidth < 1120;
        SourcesColumn.Width = new GridLength(narrow ? 0 : 200);
        SourcesPanel.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Добавить исходные файлы", Multiselect = true };
        if (dialog.ShowDialog(this) == true)
            await ScanAsync(plan.SourcePaths.Concat(dialog.FileNames));
    }

    private async void AddFolders_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Добавить исходные папки", Multiselect = true };
        if (dialog.ShowDialog(this) == true)
            await ScanAsync(plan.SourcePaths.Concat(dialog.FolderNames));
    }

    private async void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is string source)
            await ScanAsync(plan.SourcePaths.Where(path => !string.Equals(path, source, StringComparison.OrdinalIgnoreCase)));
    }

    private void SourcesMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        menu.Items.Add(new MenuItem { Header = "Убрать источник из плана", IsEnabled = false });
        foreach (string source in plan.SourcePaths)
        {
            var item = new MenuItem { Header = source, ToolTip = "Файлы останутся на месте." };
            item.Click += async (_, _) => await ScanAsync(plan.SourcePaths.Where(path =>
                !string.Equals(path, source, StringComparison.OrdinalIgnoreCase)));
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (operation is null && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            e.Handled = true;
            await ScanAsync(plan.SourcePaths.Concat(paths));
        }
    }

    private async Task ScanAsync(IEnumerable<string> sources)
    {
        string[] roots = sources.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!TryReadRules(out PackageRules rules, out string? error))
        {
            StatusText.Text = error;
            return;
        }

        RecipeEdit[] prior = plan.Items.Select(item => new RecipeEdit(
            item.SourcePath, item.ArchivePath, item.Included, item.ExclusionReason)).ToArray();
        await RunOperation(async (progress, cancellationToken) =>
        {
            PackagePlan scanned = await Task.Run(() => PackagePlanner.ScanAsync(
                roots, rules, progress, cancellationToken), cancellationToken);
            var bySource = prior.ToDictionary(edit => edit.SourcePath, StringComparer.OrdinalIgnoreCase);
            plan = scanned with
            {
                Items = scanned.Items.Select(item => bySource.TryGetValue(item.SourcePath, out var edit)
                    ? item with { ArchivePath = edit.ArchivePath, Included = edit.Included, ExclusionReason = edit.ExclusionReason }
                    : item).ToImmutableArray()
            };
            edits.Clear();
            lastOutput = null;
            RefreshPlan();
            StatusText.Text = plan.Issues.IsEmpty
                ? "Опись обновлена. Имена в комплекте можно изменить, не затрагивая оригиналы."
                : $"Требует решения: {plan.Issues[0].Message}";
        });
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (initialized && !refreshing)
            RefreshView();
    }

    private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initialized && !refreshing)
            RefreshView();
    }

    private void Requirements_Changed(object sender, TextChangedEventArgs e)
    {
        if (initialized && !refreshing)
        {
            lastOutput = null;
            RefreshPlan();
        }
    }

    private void Destination_Changed(object sender, TextChangedEventArgs e)
    {
        if (initialized && !refreshing)
        {
            lastOutput = null;
            RefreshBuildAvailability();
        }
    }

    private void Format_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initialized && !refreshing)
        {
            lastOutput = null;
            RefreshBuildAvailability();
        }
    }

    private void BrowseDestination_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Где создать новый комплект" };
        if (dialog.ShowDialog(this) == true)
            DestinationBox.Text = dialog.FolderName;
    }

    private void FilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized)
            return;

        var row = FilesGrid.SelectedItem as PlanRow;
        EditPanel.IsEnabled = row is not null && operation is null;
        SelectedSourceText.Text = row?.Item.SourcePath ?? "Выберите строку в описи.";
        ArchivePathBox.Text = row?.ArchivePath ?? string.Empty;
        ExclusionReasonBox.Text = row?.Item.ExclusionReason ?? string.Empty;
        ToggleIncludedButton.Content = row?.Item.Included == false ? "Включить в комплект" : "Исключить из комплекта";
    }

    private void ApplyName_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is not PlanRow row)
            return;

        RememberEdit();
        plan = plan with { Items = plan.Items.Select(item => item == row.Item
            ? item with { ArchivePath = ArchivePathBox.Text }
            : item).ToImmutableArray() };
        lastOutput = null;
        RefreshPlan();
        StatusText.Text = "Изменено имя копии. Исходный файл не переименован.";
    }

    private void ToggleIncluded_Click(object sender, RoutedEventArgs e)
    {
        var selected = FilesGrid.SelectedItems.Cast<PlanRow>().Select(row => row.Item).ToHashSet();
        if (selected.Count == 0)
            return;

        bool include = selected.All(item => !item.Included);
        string reason = string.IsNullOrWhiteSpace(ExclusionReasonBox.Text)
            ? "Исключено пользователем" : ExclusionReasonBox.Text.Trim();
        RememberEdit();
        plan = plan with { Items = plan.Items.Select(item => selected.Contains(item)
            ? item with { Included = include, ExclusionReason = include ? null : reason }
            : item).ToImmutableArray() };
        lastOutput = null;
        RefreshPlan();
        StatusText.Text = include ? "Выбранные позиции включены в план." : "Выбранные позиции исключены. Исходники не удалены.";
    }

    private void RememberEdit()
    {
        if (edits.Count == 32)
            edits.RemoveAt(0);
        edits.Add(plan.Items);
    }

    private async void Build_Click(object sender, RoutedEventArgs e)
    {
        RefreshPlan();
        string? output = GetOutputPath(out string? error);
        if (!plan.CanBuild || ruleError is not null || output is null)
        {
            StatusText.Text = ruleError ?? error ?? plan.Issues.FirstOrDefault()?.Message ?? "Добавьте материалы.";
            return;
        }

        PackagePlan snapshot = plan;
        PackageFormat format = SelectedFormat;
        await RunOperation(async (progress, cancellationToken) =>
        {
            PackageResult result = await Task.Run(() => PackageWriter.WriteAsync(
                snapshot, output, format, progress, cancellationToken), cancellationToken);
            lastOutput = result.OutputPath;
            StatusText.Text = $"Комплект проверен. Файлов: {result.FileCount}, размер: {FormatBytes(result.OutputBytes)}. " +
                $"{result.VerifiedAt.ToLocalTime():HH:mm:ss}  {result.OutputPath}";
        });
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        operation?.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = "Останавливаем операцию. Незавершённый результат не получит итоговое имя.";
    }

    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        string? path;
        PackageFormat format = SelectedFormat;
        if (format == PackageFormat.Zip)
        {
            var dialog = new OpenFileDialog { Title = "Проверить ZIP-комплект", Filter = "ZIP-комплект (*.zip)|*.zip" };
            if (dialog.ShowDialog(this) != true)
                return;
            path = dialog.FileName;
        }
        else
        {
            var dialog = new OpenFolderDialog { Title = "Проверить папку комплекта" };
            if (dialog.ShowDialog(this) != true)
                return;
            path = dialog.FolderName;
        }

        await RunOperation(async (progress, cancellationToken) =>
        {
            VerificationResult result = await Task.Run(() => PackageVerifier.VerifyAsync(
                path, format, progress, cancellationToken), cancellationToken);
            lastOutput = result.IsValid ? path : null;
            StatusText.Text = result.IsValid
                ? $"Комплект проверен. Файлов: {result.FileCount}, материалы: {FormatBytes(result.TotalBytes)}. {path}"
                : $"Проверка не пройдена. {string.Join("; ", result.Issues.Take(5).Select(issue => issue.Message))}";
            if (!result.IsValid)
                MessageBox.Show(this, string.Join(Environment.NewLine, result.Issues.Take(30)
                    .Select(issue => $"{issue.Path}: {issue.Message}")), "Содержимое не совпало с описью", MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    private void ShowOutput_Click(object sender, RoutedEventArgs e)
    {
        if (lastOutput is null)
            return;

        string? folder = Directory.Exists(lastOutput) ? lastOutput : Path.GetDirectoryName(lastOutput);
        if (folder is null || !Directory.Exists(folder))
        {
            StatusText.Text = "Папка результата больше не существует.";
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false });
    }

    private async Task RunOperation(Func<IProgress<PackageProgress>, CancellationToken, Task> action)
    {
        if (operation is not null)
            return;

        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        lastOutput = null;
        MainEditor.IsEnabled = false;
        BuildButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        ShowOutputButton.IsEnabled = false;
        ProgressBar.IsIndeterminate = true;
        PackagePhase? previousPhase = null;
        int previousCount = -1;
        var progress = new Progress<PackageProgress>(value =>
        {
            if (operation != cancellation || (previousPhase == value.Phase && previousCount == value.FilesCompleted))
                return;

            previousPhase = value.Phase;
            previousCount = value.FilesCompleted;
            string phase = value.Phase switch
            {
                PackagePhase.Scanning => "Читаем список файлов",
                PackagePhase.Copying => "Копирование",
                PackagePhase.Verifying => "Проверка содержимого",
                PackagePhase.Finalizing => "Завершение",
                _ => throw new InvalidOperationException("Неизвестная фаза операции.")
            };
            ProgressBar.IsIndeterminate = value.TotalFiles <= 0;
            ProgressBar.Maximum = Math.Max(1, value.TotalFiles);
            ProgressBar.Value = Math.Clamp(value.FilesCompleted, 0, Math.Max(1, value.TotalFiles));
            StatusText.Text = $"{phase}: {value.FilesCompleted}" +
                (value.TotalFiles > 0 ? $" из {value.TotalFiles}" : string.Empty) +
                (value.Path is not null ? $". {value.Path}" : string.Empty);
        });

        try
        {
            await action(progress, cancellation.Token);
        }
        catch (PackageCanceledException exception)
        {
            StatusText.Text = $"{exception.Message} Незавершённый результат: {exception.PartialPath ?? "не создавался"}";
        }
        catch (OperationCanceledException exception)
        {
            StatusText.Text = exception.Data["PartialPath"] is string partial
                ? $"Запись рецепта отменена. Незавершённая запись: {partial}"
                : "Операция отменена. Предыдущий план сохранён.";
        }
        catch (PackageWriteException exception)
        {
            StatusText.Text = $"Комплект не завершён: {exception.Message} Временный путь: {exception.PartialPath ?? "не создавался"}";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException)
        {
            StatusText.Text = $"Операция не завершена: {exception.Message}";
        }
        finally
        {
            operation = null;
            MainEditor.IsEnabled = true;
            CancelButton.IsEnabled = false;
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = 0;
            RefreshView();
        }
    }

    private void RefreshPlan()
    {
        if (TryReadRules(out PackageRules rules, out ruleError))
            plan = PackagePlanner.Validate(plan with { Rules = rules });
        RefreshView();
    }

    private void RefreshView()
    {
        string? selected = (FilesGrid.SelectedItem as PlanRow)?.Item.SourcePath;
        string search = SearchBox.Text;
        int filter = FilterBox.SelectedIndex;
        var issuesByPath = plan.Issues.Where(issue => issue.Path is not null)
            .ToLookup(issue => issue.Path!, StringComparer.OrdinalIgnoreCase);
        var rows = plan.Items.Select(item =>
        {
            string[] messages = issuesByPath[item.SourcePath].Concat(issuesByPath[item.ArchivePath])
                .Distinct().Select(issue => issue.Message).ToArray();
            string state = !item.Included ? $"Исключён: {item.ExclusionReason}" : item.SourceError ??
                (messages.Length > 0 ? string.Join("; ", messages) : "Включён");
            return new PlanRow(item, item.SourceRelativePath, item.ArchivePath, FormatBytes(item.Length), state,
                item.Included && (item.SourceError is not null || messages.Length > 0));
        }).Where(row => (filter != 1 || row.HasIssue) && (filter != 2 || !row.Item.Included))
            .Where(row => search.Length == 0 || row.Source.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                row.ArchivePath.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();

        FilesGrid.ItemsSource = rows;
        FilesGrid.SelectedItem = rows.FirstOrDefault(row => row.Item.SourcePath == selected);
        SourcesList.ItemsSource = plan.SourcePaths;
        int included = plan.Items.Count(item => item.Included);
        SummaryText.Text = plan.SourcePaths.IsEmpty && ruleError is null
            ? "Добавьте материалы - здесь появятся состав и размер комплекта."
            : $"Включено: {included}   Исключено: {plan.Items.Length - included}\nМатериалы: {FormatBytes(plan.TotalBytes)}" +
            (ruleError is not null ? $"\n{ruleError}" : plan.Issues.Length > 0 ? $"\nТребует решения: {plan.Issues.Length}. {plan.Issues[0].Message}" : string.Empty);
        RefreshBuildAvailability();
    }

    private void RefreshBuildAvailability()
    {
        string? output = GetOutputPath(out string? error);
        BuildButton.IsEnabled = operation is null && plan.CanBuild && ruleError is null && output is not null;
        BuildButton.ToolTip = error ?? output;
        OutputHintText.Text = error ?? output ?? string.Empty;
        ShowOutputButton.IsEnabled = operation is null && lastOutput is not null;
    }

    private PackageFormat SelectedFormat => FormatBox.SelectedIndex == 1 ? PackageFormat.Folder : PackageFormat.Zip;

    private string? GetOutputPath(out string? error)
    {
        error = null;
        string name = PackageNameBox.Text;
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
        {
            error = "Введите имя нового комплекта без разделителей пути и пробелов по краям.";
            return null;
        }
        if (!Path.IsPathFullyQualified(DestinationBox.Text) || !Directory.Exists(DestinationBox.Text))
        {
            error = "Выберите существующую папку для нового результата.";
            return null;
        }
        if (SelectedFormat == PackageFormat.Zip && !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            name += ".zip";
        string output = Path.Combine(DestinationBox.Text, name);
        if (File.Exists(output) || Directory.Exists(output))
        {
            error = "Такой результат уже существует. Укажите другое имя; перезапись запрещена.";
            return null;
        }
        return output;
    }

    private bool TryReadRules(out PackageRules rules, out string? error)
    {
        rules = plan.Rules;
        error = null;
        if (!TryReadMebibytes(MaxFileBox.Text, out long? maxFile) ||
            !TryReadMebibytes(MaxPayloadBox.Text, out long? maxPayload) ||
            !TryReadMebibytes(MaxPackageBox.Text, out long? maxOutput))
        {
            error = "Лимит задаётся положительным числом МиБ. Пустое поле означает отсутствие ограничения.";
            return false;
        }
        var extensions = ExtensionsBox.Text.Split([' ', ',', ';', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(extension => extension.StartsWith('.') ? extension : $".{extension}").ToImmutableArray();
        var required = RequiredPathsBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Trim()).Where(path => path.Length > 0).ToImmutableArray();
        rules = new(extensions, maxFile, maxPayload, maxOutput, required);
        return true;
    }

    private static bool TryReadMebibytes(string text, out long? bytes)
    {
        bytes = null;
        if (string.IsNullOrWhiteSpace(text))
            return true;
        var culture = text.Contains('.') ? CultureInfo.InvariantCulture : Russian;
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite,
            culture, out decimal value) || value <= 0 || value > long.MaxValue / 1_048_576m)
            return false;
        bytes = decimal.ToInt64(decimal.Floor(value * 1_048_576m));
        return bytes > 0;
    }

    private static string FormatBytes(long value) => value switch
    {
        < 1024 => $"{value:N0} Б",
        < 1_048_576 => $"{value / 1024d:N1} КиБ",
        < 1_073_741_824 => $"{value / 1_048_576d:N1} МиБ",
        _ => $"{value / 1_073_741_824d:N2} ГиБ"
    };

    private async void SaveRecipe_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadRules(out PackageRules rules, out string? error))
        {
            StatusText.Text = error;
            return;
        }
        var dialog = new SaveFileDialog { Title = "Сохранить локальный рецепт", DefaultExt = ".handinpack.json", Filter = "Рецепт Handinpack (*.handinpack.json)|*.handinpack.json" };
        if (dialog.ShowDialog(this) != true)
            return;
        var recipe = new Recipe(1, PackageNameBox.Text, DestinationBox.Text, SelectedFormat, rules,
            plan.SourcePaths.ToArray(), plan.Items.Select(item => new RecipeEdit(item.SourcePath, item.ArchivePath, item.Included, item.ExclusionReason)).ToArray());
        await RunOperation(async (_, token) =>
        {
            if (recipe.SourcePaths.Length > 10_000)
                throw new InvalidDataException("В одном рецепте поддерживается не более 10 000 источников.");
            if (recipe.SourcePaths.Any(path => !IsLocalRecipePath(path)) ||
                (recipe.DestinationDirectory.Length > 0 && !IsLocalRecipePath(recipe.DestinationDirectory)))
                throw new InvalidDataException("В рецепт можно сохранить только локальные пути без ссылок и облачных заполнителей.");
            if (File.Exists(dialog.FileName) || Directory.Exists(dialog.FileName))
                throw new IOException("Рецепт с таким именем уже существует. Выберите новое имя.");
            string partial = Path.Combine(Path.GetDirectoryName(dialog.FileName)!, $".handinpack-recipe-{Guid.NewGuid():N}.partial");
            try
            {
                await using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                {
                    await JsonSerializer.SerializeAsync(stream, recipe, RecipeJson, token);
                    if (stream.Length > 16 * 1_048_576)
                        throw new IOException("Рецепт превышает ограничение 16 МиБ.");
                    await stream.FlushAsync(token);
                }
                token.ThrowIfCancellationRequested();
                File.Move(partial, dialog.FileName, overwrite: false);
            }
            catch (OperationCanceledException exception)
            {
                exception.Data["PartialPath"] = partial;
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Рецепт не сохранён. Незавершённая запись: {partial}. {exception.Message}", exception);
            }
            StatusText.Text = $"Рецепт сохранён: {dialog.FileName}. Он содержит локальные пути и не входит в комплект.";
        });
    }

    private async void OpenRecipe_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Открыть локальный рецепт", Filter = "Рецепт Handinpack (*.handinpack.json)|*.handinpack.json" };
        if (dialog.ShowDialog(this) != true)
            return;
        await RunOperation(async (progress, token) =>
        {
            Recipe recipe;
            await using (var stream = new FileStream(dialog.FileName, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
            {
                if (stream.Length > 16 * 1_048_576)
                    throw new InvalidDataException("Рецепт превышает ограничение 16 МиБ.");
                recipe = await JsonSerializer.DeserializeAsync<Recipe>(stream, RecipeJson, token)
                    ?? throw new InvalidDataException("Рецепт пуст.");
            }
            if (recipe.Version != 1 || !Enum.IsDefined(recipe.Format) || recipe.SourcePaths.Length > 10_000 ||
                recipe.SourcePaths.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) ||
                recipe.Edits.Any(edit => edit is null || string.IsNullOrWhiteSpace(edit.SourcePath)) ||
                (!recipe.Rules.AllowedExtensions.IsDefault && recipe.Rules.AllowedExtensions.Any(string.IsNullOrWhiteSpace)) ||
                (!recipe.Rules.RequiredPaths.IsDefault && recipe.Rules.RequiredPaths.Any(string.IsNullOrWhiteSpace)) ||
                recipe.Edits.Select(edit => edit.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != recipe.Edits.Length)
                throw new InvalidDataException("Некорректная или неподдерживаемая версия рецепта.");

            if (recipe.SourcePaths.Any(path => !IsLocalRecipePath(path)) ||
                (recipe.DestinationDirectory.Length > 0 && !IsLocalRecipePath(recipe.DestinationDirectory)))
                throw new InvalidDataException("Сохранённый набор может ссылаться только на локальные диски. Сетевые и специальные пути не открываются автоматически.");

            PackagePlan scanned = await Task.Run(() => PackagePlanner.ScanAsync(recipe.SourcePaths,
                recipe.Rules, progress, token), token);
            var bySource = recipe.Edits.ToDictionary(edit => edit.SourcePath, StringComparer.OrdinalIgnoreCase);
            PackagePlan restored = PackagePlanner.Validate(scanned with
            {
                Items = scanned.Items.Select(item => bySource.TryGetValue(item.SourcePath, out var edit)
                    ? item with { ArchivePath = edit.ArchivePath, Included = edit.Included, ExclusionReason = edit.ExclusionReason }
                    : item).ToImmutableArray()
            });
            token.ThrowIfCancellationRequested();
            refreshing = true;
            try
            {
                PackageNameBox.Text = recipe.PackageName;
                DestinationBox.Text = recipe.DestinationDirectory;
                FormatBox.SelectedIndex = recipe.Format == PackageFormat.Zip ? 0 : 1;
                ExtensionsBox.Text = string.Join(" ", recipe.Rules.AllowedExtensions.IsDefault ? [] : recipe.Rules.AllowedExtensions);
                RequiredPathsBox.Text = string.Join(Environment.NewLine, recipe.Rules.RequiredPaths.IsDefault ? [] : recipe.Rules.RequiredPaths);
                MaxFileBox.Text = recipe.Rules.MaxFileBytes is long fileBytes ? (fileBytes / 1_048_576m).ToString(Russian) : string.Empty;
                MaxPayloadBox.Text = recipe.Rules.MaxTotalBytes is long payloadBytes ? (payloadBytes / 1_048_576m).ToString(Russian) : string.Empty;
                MaxPackageBox.Text = recipe.Rules.MaxOutputBytes is long outputBytes ? (outputBytes / 1_048_576m).ToString(Russian) : string.Empty;
                plan = restored;
                edits.Clear();
                lastOutput = null;
                ruleError = null;
            }
            finally
            {
                refreshing = false;
            }
            StatusText.Text = "Рецепт открыт. Исходники перечитаны; проверьте актуальную опись перед сборкой.";
        });
    }

    private static bool IsLocalRecipePath(string path)
    {
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' ||
            (path[2] != '\\' && path[2] != '/'))
            return false;
        if (new DriveInfo(path[..3]).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram or DriveType.CDRom))
            return false;
        string fullPath = Path.GetFullPath(path);
        string current = Path.GetPathRoot(fullPath)!;
        foreach (string segment in fullPath[current.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                if ((File.GetAttributes(current) & (FileAttributes.ReparsePoint | FileAttributes.Offline |
                    (FileAttributes)0x40000 | (FileAttributes)0x400000)) != 0)
                    return false;
            }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
        }
        return true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!initialized || operation is not null)
            return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z && Keyboard.FocusedElement is not TextBox && edits.Count > 0)
        {
            var previous = edits[^1];
            edits.RemoveAt(edits.Count - 1);
            plan = plan with { Items = previous };
            lastOutput = null;
            RefreshPlan();
            StatusText.Text = "Последнее изменение имени или включения отменено.";
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && FilesGrid.IsKeyboardFocusWithin && FilesGrid.SelectedItem is not null)
        {
            ArchivePathBox.Focus();
            ArchivePathBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Space && FilesGrid.IsKeyboardFocusWithin)
        {
            ToggleIncluded_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.F6)
        {
            if (FilesGrid.IsKeyboardFocusWithin)
                PackageNameBox.Focus();
            else
                FilesGrid.Focus();
            e.Handled = true;
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (operation is not null)
        {
            e.Cancel = true;
            operation.Cancel();
            StatusText.Text = "Останавливаем операцию. После остановки окно можно закрыть.";
        }
    }

    private sealed record PlanRow(PackageItem Item, string Source, string ArchivePath, string SizeText, string StateText, bool HasIssue);
    private sealed record RecipeEdit(string SourcePath, string ArchivePath, bool Included, string? ExclusionReason);
    private sealed record Recipe(int Version, string PackageName, string DestinationDirectory, PackageFormat Format,
        PackageRules Rules, string[] SourcePaths, RecipeEdit[] Edits);
}

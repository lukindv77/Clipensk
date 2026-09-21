using System.Diagnostics;
using Clipensk.Core.Settings;
using Clipensk.Infrastructure.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Clipensk.App;

public sealed partial class JournalWindow
{
    private sealed record LocalizationFileOption(string DisplayName, string? FileName);

    private void InitializeLocalizationEditor() => LoadLocalizationEditor();

    /// <summary>
    /// Repopulates the list of discoverable <c>*.json</c> files under
    /// <c>&lt;DataRoot&gt;\Languages\</c>, per <c>docs/REQUIREMENTS.md</c> §20's "либо поместить в
    /// специальную папку Languages": a file dropped there manually, outside this app, becomes
    /// selectable once this runs, whether from reopening Settings or an explicit "reread" click.
    /// This never applies or persists anything by itself — it only refreshes what is offered.
    /// </summary>
    private void LoadLocalizationEditor()
    {
        IReadOnlyList<LocalizationFileOption> options = BuildLocalizationFileOptions();
        LocalizationFileComboBox.ItemsSource = options;
        LocalizationFileComboBox.SelectedItem = options.FirstOrDefault(
            option => option.FileName == _settings.ActiveLocalizationFileName) ?? options[0];
    }

    private IReadOnlyList<LocalizationFileOption> BuildLocalizationFileOptions()
    {
        var options = new List<LocalizationFileOption>
        {
            new(_localization.GetString("Settings.Localization.BuiltIn"), null),
        };

        try
        {
            string languagesDirectory = GetLanguagesDirectory();
            Directory.CreateDirectory(languagesDirectory);
            foreach (string filePath in Directory
                .EnumerateFiles(languagesDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                string fileName = Path.GetFileName(filePath);
                options.Add(new LocalizationFileOption(fileName, fileName));
            }
        }
        catch (Exception)
        {
            // Папка Languages недоступна (например, диск отключён): в списке остаётся только
            // встроенный русский вариант. Инициализация экрана настроек не должна падать из-за этого.
        }

        return options;
    }

    private string GetLanguagesDirectory() => Path.Combine(
        _settings.DataRootPath ?? throw new InvalidOperationException("DataRoot не настроен."),
        "Languages");

    private async void OnLoadLocalizationFileClicked(object sender, RoutedEventArgs e)
    {
        LoadLocalizationFileButton.IsEnabled = false;
        LocalizationInfo.IsOpen = false;

        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add(".json");

            nint windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);

            StorageFile? file;
            _systemPickerOpen = true;
            try
            {
                file = await picker.PickSingleFileAsync();
            }
            finally
            {
                _systemPickerOpen = false;
            }

            if (file is null)
            {
                return;
            }

            IReadOnlyDictionary<string, string> overlay;
            try
            {
                overlay = await new JsonExternalLocalizationLoader().LoadAsync(file.Path);
            }
            catch (Exception)
            {
                // Проверяем файл до того, как он попадёт в Languages: повреждённый перевод не должен
                // затирать уже рабочий файл с тем же именем.
                LocalizationInfo.Severity = InfoBarSeverity.Error;
                LocalizationInfo.Message = _localization.GetString("Settings.Localization.LoadFailed");
                LocalizationInfo.IsOpen = true;
                return;
            }

            string languagesDirectory = GetLanguagesDirectory();
            Directory.CreateDirectory(languagesDirectory);
            string fileName = Path.GetFileName(file.Path);
            File.Copy(file.Path, Path.Combine(languagesDirectory, fileName), overwrite: true);

            ApplicationSettings updated = _settings with { ActiveLocalizationFileName = fileName };
            await _settingsStore.SaveAsync(updated);
            _settings = updated;
            _localization.SetOverlay(overlay);

            LoadLocalizationEditor();

            LocalizationInfo.Severity = InfoBarSeverity.Success;
            LocalizationInfo.Message = _localization.GetString("Settings.Localization.Loaded");
            LocalizationInfo.IsOpen = true;
        }
        catch (Exception)
        {
            LocalizationInfo.Severity = InfoBarSeverity.Error;
            LocalizationInfo.Message = _localization.GetString("Settings.Localization.LoadFailed");
            LocalizationInfo.IsOpen = true;
        }
        finally
        {
            LoadLocalizationFileButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Writes <c>translation-template.json</c> into <c>&lt;DataRoot&gt;\Languages\</c>, per the
    /// product decision in <c>docs/OPEN_QUESTIONS.md</c> §9: every built-in key, with the built-in
    /// Russian text as both the starting value and an app-generated <c>//</c> comment for context.
    /// Re-clicking regenerates the file from the current built-in strings, so it never goes stale
    /// after this build adds or changes a key.
    /// </summary>
    private async void OnExportLocalizationTemplateClicked(object sender, RoutedEventArgs e)
    {
        ExportLocalizationTemplateButton.IsEnabled = false;
        LocalizationInfo.IsOpen = false;

        try
        {
            string languagesDirectory = GetLanguagesDirectory();
            Directory.CreateDirectory(languagesDirectory);
            string filePath = Path.Combine(languagesDirectory, "translation-template.json");
            await LocalizationTemplateWriter.WriteAsync(filePath, BuiltInRussianLocalizationService.AllStrings);

            LoadLocalizationEditor();

            LocalizationInfo.Severity = InfoBarSeverity.Success;
            LocalizationInfo.Message = _localization.GetString("Settings.Localization.TemplateExported");
            LocalizationInfo.IsOpen = true;
        }
        catch (Exception)
        {
            LocalizationInfo.Severity = InfoBarSeverity.Error;
            LocalizationInfo.Message = _localization.GetString("Settings.Localization.TemplateExportFailed");
            LocalizationInfo.IsOpen = true;
        }
        finally
        {
            ExportLocalizationTemplateButton.IsEnabled = true;
        }
    }

    private void OnOpenLanguagesFolderClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            string languagesDirectory = GetLanguagesDirectory();
            Directory.CreateDirectory(languagesDirectory);
            Process.Start(new ProcessStartInfo(languagesDirectory) { UseShellExecute = true });
        }
        catch (Exception)
        {
            LocalizationInfo.Severity = InfoBarSeverity.Error;
            LocalizationInfo.Message = _localization.GetString("Settings.Localization.FolderOpenFailed");
            LocalizationInfo.IsOpen = true;
        }
    }

    /// <summary>
    /// Refreshes the discoverable file list and reloads the currently active file's content from
    /// disk, per <c>docs/REQUIREMENTS.md</c> §20's "перечитывание переводов". This never writes to
    /// <see cref="ApplicationSettings"/>: if the active file went missing since it was selected, the
    /// live overlay falls back to built-in for this session, but the persisted selection is left for
    /// the user to explicitly change via Save, not silently rewritten by a read-only refresh.
    /// </summary>
    private async void OnRereadLocalizationClicked(object sender, RoutedEventArgs e)
    {
        RereadLocalizationButton.IsEnabled = false;
        LocalizationInfo.IsOpen = false;

        try
        {
            LoadLocalizationEditor();

            if (_settings.ActiveLocalizationFileName is not { } fileName)
            {
                _localization.SetOverlay(null);
                LocalizationInfo.Severity = InfoBarSeverity.Success;
                LocalizationInfo.Message = _localization.GetString("Settings.Localization.RereadCompleted");
                LocalizationInfo.IsOpen = true;
                return;
            }

            try
            {
                string filePath = Path.Combine(GetLanguagesDirectory(), fileName);
                IReadOnlyDictionary<string, string> overlay =
                    await new JsonExternalLocalizationLoader().LoadAsync(filePath);
                _localization.SetOverlay(overlay);

                LocalizationInfo.Severity = InfoBarSeverity.Success;
                LocalizationInfo.Message = _localization.GetString("Settings.Localization.RereadCompleted");
                LocalizationInfo.IsOpen = true;
            }
            catch (Exception)
            {
                _localization.SetOverlay(null);
                LocalizationInfo.Severity = InfoBarSeverity.Error;
                LocalizationInfo.Message = _localization.GetString("Settings.Localization.ActiveFileMissing");
                LocalizationInfo.IsOpen = true;
            }
        }
        finally
        {
            RereadLocalizationButton.IsEnabled = true;
        }
    }

    private async void OnSaveLocalizationClicked(object sender, RoutedEventArgs e)
    {
        if (LocalizationFileComboBox.SelectedItem is not LocalizationFileOption selected)
        {
            return;
        }

        try
        {
            IReadOnlyDictionary<string, string>? overlay = null;
            if (selected.FileName is { } fileName)
            {
                string filePath = Path.Combine(GetLanguagesDirectory(), fileName);
                overlay = await new JsonExternalLocalizationLoader().LoadAsync(filePath);
            }

            ApplicationSettings updated = _settings with { ActiveLocalizationFileName = selected.FileName };
            await _settingsStore.SaveAsync(updated);
            _settings = updated;
            _localization.SetOverlay(overlay);

            LocalizationInfo.Severity = InfoBarSeverity.Success;
            LocalizationInfo.Message = _localization.GetString("Settings.Localization.Saved");
            LocalizationInfo.IsOpen = true;
        }
        catch (Exception)
        {
            LocalizationInfo.Severity = InfoBarSeverity.Error;
            LocalizationInfo.Message = _localization.GetString("Settings.Localization.SaveFailed");
            LocalizationInfo.IsOpen = true;
        }
    }
}

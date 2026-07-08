using System;
using System.Collections.ObjectModel;
using System.Linq;
using MasselGUARD;
using MasselGUARD.Infrastructure;
using MasselGUARD.Models;
using MasselGUARD.Services;

namespace MasselGUARD.ViewModels
{
    /// <summary>
    /// ViewModel for WizardWindow.
    /// Manages the 6-step wizard flow.
    /// The View binds to CurrentStep and navigates via commands.
    /// </summary>
    public class WizardViewModel : ObservableObject
    {
        private readonly ConfigService _config;
        private readonly LogService    _log;

        public const int TotalSteps = 8;

        private int _step;
        public int Step
        {
            get => _step;
            private set
            {
                SetField(ref _step, value);
                OnPropertyChanged(nameof(IsFirstStep));
                OnPropertyChanged(nameof(IsLastStep));
                OnPropertyChanged(nameof(CanGoBack));
                OnPropertyChanged(nameof(NextLabel));
                BackCommand.RaiseCanExecuteChanged();
                NextCommand.RaiseCanExecuteChanged();
            }
        }

        public bool IsFirstStep => _step == 0;
        public bool IsLastStep  => _step == TotalSteps - 1;
        public bool CanGoBack   => _step > 0;
        public string NextLabel => IsLastStep ? "Finish" : "Next";

        // ── Step 1: Language ──────────────────────────────────────────────────
        public ObservableCollection<MasselGUARD.LangItem> AvailableLanguages { get; } = new();

        private MasselGUARD.LangItem? _selectedLanguage;
        public  MasselGUARD.LangItem?  SelectedLanguage
        {
            get => _selectedLanguage;
            set
            {
                if (!SetField(ref _selectedLanguage, value)) return;
                if (value != null)
                {
                    _config.Config.Language = value.Code.ToLowerInvariant();
                    _pendingLangChanged?.Invoke(value.Code);
                }
            }
        }

        // ── Step 2: Mode ──────────────────────────────────────────────────────
        private AppMode _mode = AppMode.Standalone;
        public AppMode Mode
        {
            get => _mode;
            set => SetField(ref _mode, value);
        }

        // ── Step 3: Disable WiFi rules ────────────────────────────────────────
        private bool _disableWifiRules;
        public bool DisableWifiRules
        {
            get => _disableWifiRules;
            set => SetField(ref _disableWifiRules, value);
        }

        // ── Step 5: About card ────────────────────────────────────────────────
        public string AppVersion         => UpdateChecker.CurrentVersionString;
        public string PreviousAppVersion => _config.Config.LastRunVersion ?? "unknown";
        public string UpdateStatus { get; private set; } = "Not checked";

        // ── Commands ──────────────────────────────────────────────────────────
        public RelayCommand      BackCommand         { get; }
        public RelayCommand      NextCommand         { get; }
        public RelayCommand      SkipCommand         { get; }
        public AsyncRelayCommand CheckUpdateCommand  { get; }

        // ── Events ────────────────────────────────────────────────────────────
        public event Action? Finished;
        public event Action? Skipped;

        private Action<string>? _pendingLangChanged;

        // ── Constructor ───────────────────────────────────────────────────────
        public WizardViewModel(ConfigService config, LogService log,
            Action<string>? onLangChanged = null)
        {
            _config             = config;
            _log                = log;
            _pendingLangChanged = onLangChanged;

            _mode             = config.Config.Mode;
            _disableWifiRules = config.Config.ManualMode;

            BackCommand        = new RelayCommand(GoBack,  () => CanGoBack);
            NextCommand        = new RelayCommand(GoNext);
            SkipCommand        = new RelayCommand(() => Skipped?.Invoke());
            CheckUpdateCommand = new AsyncRelayCommand(CheckUpdate);

            PopulateLanguages();
        }

        /// <summary>Re-sync VM state from config after an import.</summary>
        public void LoadFromConfig()
        {
            _mode             = _config.Config.Mode;
            _disableWifiRules = _config.Config.ManualMode;
            OnPropertyChanged(nameof(Mode));
            OnPropertyChanged(nameof(DisableWifiRules));
            // Re-select the imported language
            var match = AvailableLanguages.FirstOrDefault(
                l => l.Code == _config.Config.Language);
            if (match != null) SelectedLanguage = match;
        }

        // ── Navigation ────────────────────────────────────────────────────────

        // Step 5 (WiFi Automation) has nothing left to configure once WiFi rules are
        // disabled (from the Step 2 "Manual" preset or the Step 5 toggle itself) — skip
        // over it in both directions rather than showing an empty/redundant step.
        private const int WifiAutomationStep = 5;

        private void GoBack()
        {
            if (_step <= 0) return;
            int prev = _step - 1;
            if (prev == WifiAutomationStep && _disableWifiRules) prev--;
            Step = Math.Max(prev, 0);
        }

        private void GoNext()
        {
            if (IsLastStep)
            {
                ApplyAndFinish();
                return;
            }
            int next = _step + 1;
            if (next == WifiAutomationStep && _disableWifiRules) next++;
            Step = next;
        }

        private void ApplyAndFinish()
        {
            var cfg        = _config.Config;
            cfg.Mode       = _mode;
            cfg.ManualMode = _disableWifiRules;
            _config.Save();
            _log.Ok("Wizard completed");
            Finished?.Invoke();
        }

        // ── Language ──────────────────────────────────────────────────────────

        private void PopulateLanguages()
        {
            AvailableLanguages.Clear();
            foreach (var (code, name, flag) in Lang.AvailableLanguages())
            {
                var item = new MasselGUARD.LangItem(code, name, flag);
                AvailableLanguages.Add(item);
                if (string.Equals(code, _config.Config.Language,
                        StringComparison.OrdinalIgnoreCase))
                    _selectedLanguage = item;
            }
            OnPropertyChanged(nameof(SelectedLanguage));
        }

        // ── Update check ──────────────────────────────────────────────────────

        private async System.Threading.Tasks.Task CheckUpdate(object? _)
        {
            UpdateStatus = "Checking…";
            OnPropertyChanged(nameof(UpdateStatus));
            await UpdateChecker.CheckAsync(_config.Config, _config.Save);
            UpdateStatus = GetUpdateStatusText();
            OnPropertyChanged(nameof(UpdateStatus));
        }

        private string GetUpdateStatusText()
        {
            var current = UpdateChecker.CurrentVersionString;
            var latest  = _config.Config.LatestKnownVersion;
            if (string.IsNullOrEmpty(latest)) return "Not checked";
            return string.Compare(current, latest,
                StringComparison.OrdinalIgnoreCase) >= 0
                ? $"Up to date (v{current})"
                : $"Update available: v{latest}";
        }
    }
}

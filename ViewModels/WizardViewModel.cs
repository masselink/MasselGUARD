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

        public const int TotalSteps = 9;

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

        // ── Step 1: Feature modules (VPN / DNS / Both) ────────────────────────
        // Drives which later steps are shown: WireGuard Behaviour needs tunnels,
        // DNS Profiles Behaviour needs DNS. Committed on finish.
        private bool _enableTunnels;
        public bool EnableTunnels
        {
            get => _enableTunnels;
            set => SetField(ref _enableTunnels, value);
        }

        private bool _enableDns;
        public bool EnableDns
        {
            get => _enableDns;
            set => SetField(ref _enableDns, value);
        }

        // ── Step 4: Disable WiFi rules (manual mode) ──────────────────────────
        private bool _disableWifiRules;
        public bool DisableWifiRules
        {
            get => _disableWifiRules;
            set => SetField(ref _disableWifiRules, value);
        }

        // ── Step 8: About card ────────────────────────────────────────────────
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

            _disableWifiRules = config.Config.ManualMode;
            _enableTunnels    = config.Config.EnableTunnels;
            _enableDns        = config.Config.EnableDns;

            BackCommand        = new RelayCommand(GoBack,  () => CanGoBack);
            NextCommand        = new RelayCommand(GoNext);
            SkipCommand        = new RelayCommand(() => Skipped?.Invoke());
            CheckUpdateCommand = new AsyncRelayCommand(CheckUpdate);

            PopulateLanguages();
        }

        /// <summary>Re-sync VM state from config after an import.</summary>
        public void LoadFromConfig()
        {
            _disableWifiRules = _config.Config.ManualMode;
            _enableTunnels    = _config.Config.EnableTunnels;
            _enableDns        = _config.Config.EnableDns;
            OnPropertyChanged(nameof(DisableWifiRules));
            OnPropertyChanged(nameof(EnableTunnels));
            OnPropertyChanged(nameof(EnableDns));
            // Re-select the imported language
            var match = AvailableLanguages.FirstOrDefault(
                l => l.Code == _config.Config.Language);
            if (match != null) SelectedLanguage = match;
        }

        // ── Navigation ────────────────────────────────────────────────────────

        // New flow (0-8):
        //   0 Welcome + import + language   1 Feature choice (VPN/DNS/Both)   2 Appearance
        //   3 Startup                       4 WiFi settings                    5 WireGuard Behaviour
        //   6 DNS Profiles Behaviour        7 Notifications                    8 Done
        private const int WireGuardBehaviourStep = 5;   // needs tunnels
        private const int DnsBehaviourStep       = 6;   // needs DNS

        private bool IsStepSkipped(int step) => step switch
        {
            WireGuardBehaviourStep => !_enableTunnels,
            DnsBehaviourStep       => !_enableDns,
            _                      => false,
        };

        private void GoBack()
        {
            if (_step <= 0) return;
            int prev = _step - 1;
            while (prev > 0 && IsStepSkipped(prev)) prev--;
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
            while (next < TotalSteps - 1 && IsStepSkipped(next)) next++;
            Step = next;
        }

        private void ApplyAndFinish()
        {
            var cfg           = _config.Config;
            cfg.ManualMode    = _disableWifiRules;
            // At least one module must stay enabled - fall back to tunnels if somehow both off.
            cfg.EnableTunnels = _enableTunnels || !_enableDns;
            cfg.EnableDns     = _enableDns;
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

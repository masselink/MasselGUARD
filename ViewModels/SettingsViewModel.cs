using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using MasselGUARD;
using MasselGUARD.Infrastructure;
using MasselGUARD.Models;
using MasselGUARD.Services;

namespace MasselGUARD.ViewModels
{
    /// <summary>
    /// ViewModel for SettingsWindow.
    /// Each tab section exposes properties and commands.
    /// Changes are staged and applied on Save (for rules) or immediately
    /// (for toggles that have immediate effect like theme, language).
    /// </summary>
    public class SettingsViewModel : ObservableObject
    {
        private readonly ConfigService _config;
        private readonly LogService    _log;

        // ── Observable properties ─────────────────────────────────────────────

        // General
        private string _language = "";
        public string Language
        {
            get => _language;
            set
            {
                if (!SetField(ref _language, value)) return;
            }
        }

        private AppMode _mode;
        public AppMode Mode
        {
            get => _mode;
            set
            {
                if (!SetField(ref _mode, value)) return;
            }
        }

        // Default Action
        private string _defaultAction = "none";
        public string DefaultAction
        {
            get => _defaultAction;
            set
            {
                if (!SetField(ref _defaultAction, value)) return;
            }
        }

        private string _defaultTunnel = "";
        public string DefaultTunnel
        {
            get => _defaultTunnel;
            set
            {
                if (!SetField(ref _defaultTunnel, value)) return;
            }
        }

        private string _openWifiTunnel = "";
        public string OpenWifiTunnel
        {
            get => _openWifiTunnel;
            set
            {
                if (!SetField(ref _openWifiTunnel, value)) return;
                _log.Ok($"Open network tunnel: {(string.IsNullOrEmpty(value) ? "none" : value)}");
            }
        }

        // WiFi Rules
        private bool _disableWifiRules;
        public bool DisableWifiRules
        {
            get => _disableWifiRules;
            set
            {
                if (!SetField(ref _disableWifiRules, value)) return;
                _log.Ok($"WiFi rules: {(value ? "disabled" : "enabled")}");
            }
        }

        private bool _automationEnabled = true;
        public bool AutomationEnabled
        {
            get => _automationEnabled;
            set => SetField(ref _automationEnabled, value);
        }

        // Rules list (deferred save)
        public ObservableCollection<TunnelRule> Rules { get; } = new();
        private bool _rulesHaveUnsavedChanges;
        public bool RulesHaveUnsavedChanges
        {
            get => _rulesHaveUnsavedChanges;
            set => SetField(ref _rulesHaveUnsavedChanges, value);
        }

        private TunnelRule? _selectedRule;
        public TunnelRule? SelectedRule
        {
            get => _selectedRule;
            set
            {
                SetField(ref _selectedRule, value);
                EditRuleCommand.RaiseCanExecuteChanged();
                DeleteRuleCommand.RaiseCanExecuteChanged();
            }
        }

        // Appearance
        // No live-apply side effect here — this setter has no way to know the draft's
        // Light/Dark/Auto mode, and previously always resolved via the raw Windows
        // setting (ThemeManager.GetSystemIsDark()), which could disagree with what the
        // user actually picked in Settings. The view (SettingsWindow) applies the theme
        // itself via ApplySpecificTheme(forceLight: !IsDraftDark()) after setting this.
        private string _activeTheme = "__system__";
        public string ActiveTheme
        {
            get => _activeTheme;
            set => SetField(ref _activeTheme, value);
        }

        private bool _showTrayPopup;
        public bool ShowTrayPopup
        {
            get => _showTrayPopup;
            set
            {
                if (!SetField(ref _showTrayPopup, value)) return;
            }
        }

        // Advanced
        private string _logLevel = "normal";
        public string LogLevel
        {
            get => _logLevel;
            set
            {
                if (!SetField(ref _logLevel, value)) return;
            }
        }

        // Tunnel groups
        public ObservableCollection<TunnelGroup> TunnelGroups { get; } = new();

        // Available tunnel names for pickers
        public ObservableCollection<string> TunnelNames { get; } = new();

        // ── Commands ──────────────────────────────────────────────────────────

        public RelayCommand AddRuleCommand     { get; }
        public RelayCommand EditRuleCommand    { get; }
        public RelayCommand DeleteRuleCommand  { get; }
        public RelayCommand SaveRulesCommand   { get; }
        public RelayCommand SaveCommand        { get; }
        public RelayCommand ExportCommand      { get; }
        public RelayCommand ImportCommand      { get; }
        public RelayCommand AddGroupCommand    { get; }
        public RelayCommand DeleteGroupCommand { get; }

        // ── Events raised to View (dialog requests) ───────────────────────────
        public event Action?              AddRuleRequested;
        public event Action<TunnelRule>?  EditRuleRequested;
        public event Action?              ExportRequested;
        public event Action?              ImportRequested;
        public event Action<AppMode>?     ModeChanged;
        public event Action<string>?      LogLevelChanged;

        // ── Constructor ───────────────────────────────────────────────────────
        public SettingsViewModel(ConfigService config, LogService log)
        {
            _config = config;
            _log    = log;

            AddRuleCommand    = new RelayCommand(DoAddRule);
            EditRuleCommand   = new RelayCommand(DoEditRule,   () => SelectedRule != null);
            DeleteRuleCommand = new RelayCommand(DoDeleteRule, () => SelectedRule != null);
            SaveRulesCommand  = new RelayCommand(DoSaveRules);
            SaveCommand       = new RelayCommand(DoSave);
            ExportCommand     = new RelayCommand(() => ExportRequested?.Invoke());
            ImportCommand     = new RelayCommand(() => ImportRequested?.Invoke());
            AddGroupCommand   = new RelayCommand(DoAddGroup);
            DeleteGroupCommand= new RelayCommand(DoDeleteGroup);

            LoadFromConfig();
        }

        // ── Load ──────────────────────────────────────────────────────────────

        public void LoadFromConfig()
        {
            var cfg = _config.Config;

            _language         = cfg.Language;
            _mode             = cfg.Mode;
            _defaultAction    = cfg.DefaultAction;
            _defaultTunnel    = cfg.DefaultTunnel;
            _openWifiTunnel   = cfg.OpenWifiTunnel;
            _disableWifiRules = cfg.ManualMode;
            _automationEnabled= !cfg.ManualMode;
            _activeTheme      = cfg.ActiveTheme;
            _showTrayPopup    = cfg.ShowTrayPopupOnSwitch;
            _logLevel         = cfg.LogLevelSetting;

            Rules.Clear();
            foreach (var r in cfg.Rules) Rules.Add(r);

            TunnelGroups.Clear();
            foreach (var g in cfg.TunnelGroups) TunnelGroups.Add(g);

            TunnelNames.Clear();
            foreach (var t in cfg.Tunnels.Select(t => t.Name))
                TunnelNames.Add(t);

            RulesHaveUnsavedChanges = false;

            // Raise all property changed
            OnPropertyChanged(null);
        }

        // ── Rule CRUD (deferred) ──────────────────────────────────────────────

        private void DoAddRule()    => AddRuleRequested?.Invoke();
        private void DoEditRule()   { if (SelectedRule != null) EditRuleRequested?.Invoke(SelectedRule); }
        private void DoDeleteRule()
        {
            if (SelectedRule == null) return;
            Rules.Remove(SelectedRule);
            RulesHaveUnsavedChanges = true;
            SelectedRule = null;
        }

        public void AddRule(TunnelRule rule)
        {
            Rules.Add(rule);
            RulesHaveUnsavedChanges = true;
        }

        public void UpdateRule(TunnelRule rule)
        {
            RulesHaveUnsavedChanges = true;
        }

        private void DoSaveRules()
        {
            _config.Save();
            _log.Ok("WiFi rules saved");
            RulesHaveUnsavedChanges = false;
        }

        // ── Group CRUD ────────────────────────────────────────────────────────

        private void DoAddGroup()
        {
            var g = new TunnelGroup("New group");
            TunnelGroups.Add(g);
            SaveImmediate();
        }

        private void DoDeleteGroup()
        {
            // Request View to confirm which group
        }

        // ── Immediate save ────────────────────────────────────────────────────

        public void DoSave()
        {
            _config.Config.Language              = _language;
            _config.Config.Mode                  = _mode;
            _config.Config.DefaultAction         = _defaultAction;
            _config.Config.DefaultTunnel         = _defaultTunnel;
            _config.Config.OpenWifiTunnel        = _openWifiTunnel;
            _config.Config.ManualMode            = _disableWifiRules;
            _config.Config.ActiveTheme           = _activeTheme;
            _config.Config.ShowTrayPopupOnSwitch = _showTrayPopup;
            _config.Config.LogLevelSetting       = _logLevel;
            _config.Config.Rules                 = Rules.ToList();
            _config.Config.TunnelGroups          = TunnelGroups.ToList();
            _config.Save();

            // Ensure the saved theme is applied (in case preview changed it mid-session)
            {
                bool isDark = ThemeManager.GetSystemIsDark();
                ThemeManager.Instance.Load(_activeTheme, isDark);
            }

            _log.Ok("Settings saved");
            RulesHaveUnsavedChanges = false;

            // Fire side effects
            ModeChanged?.Invoke(_mode);
            LogLevelChanged?.Invoke(_logLevel);
        }

        private void SaveImmediate() { /* staged — explicit save via DoSave() */ }
    }
}

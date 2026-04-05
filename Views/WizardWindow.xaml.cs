using System;
using System.Windows;
using System.Windows.Media;

namespace MasselGUARD.Views
{
    public partial class WizardWindow : Window
    {
        private readonly MainWindow _main;
        private int   _step      = 0;
        private const int TotalSteps = 5;   // steps 0-4

        // Deferred values — only applied on Finish
        private string _pendingLang = "";

        public WizardWindow(MainWindow main)
        {
            InitializeComponent();
            _main = main;

            // Populate language picker
            foreach (var (code, name) in Lang.AvailableLanguages())
                WizLangPicker.Items.Add(new LangItem(code, name));
            WizLangPicker.DisplayMemberPath = "Name";
            foreach (LangItem item in WizLangPicker.Items)
                if (item.Code == Lang.Instance.CurrentCode)
                { WizLangPicker.SelectedItem = item; break; }

            // Pre-select current values (display only — not applied until Finish)
            var cfg = _main.GetConfig();
            WizModeStandalone.IsChecked = cfg.Mode == AppMode.Standalone;
            WizModeCompanion.IsChecked  = cfg.Mode == AppMode.Companion;
            WizModeMixed.IsChecked      = cfg.Mode == AppMode.Mixed || (
                !WizModeStandalone.IsChecked.GetValueOrDefault() &&
                !WizModeCompanion.IsChecked.GetValueOrDefault());
            WizManualToggle.IsChecked = cfg.ManualMode;

            ShowStep(0);
        }

        // ── Step navigation ───────────────────────────────────────────────────
        private void ShowStep(int step)
        {
            _step = step;

            Step0.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
            Step1.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
            Step2.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
            Step3.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
            Step4.Visibility = step == 4 ? Visibility.Visible : Visibility.Collapsed;

            var dots   = new[] { Dot0, Dot1, Dot2, Dot3, Dot4 };
            var accent = (SolidColorBrush)FindResource("Accent");
            var dim    = (SolidColorBrush)FindResource("Border");
            for (int i = 0; i < dots.Length; i++)
                dots[i].Fill = i == step ? accent : dim;

            BtnBack.IsEnabled = step > 0;
            BtnNext.Content   = step == TotalSteps - 1
                ? Lang.T("WizardBtnFinish")
                : Lang.T("WizardBtnNext");
            BtnSkip.Visibility = step == TotalSteps - 1
                ? Visibility.Collapsed : Visibility.Visible;

            if (step == 2) RefreshModeStatus();
        }

        // ── Mode status — shows DLL + WG readiness for selected mode ──────────
        private void RefreshModeStatus()
        {
            AppMode mode = AppMode.Standalone;
            if (WizModeCompanion.IsChecked == true) mode = AppMode.Companion;
            else if (WizModeMixed.IsChecked == true) mode = AppMode.Mixed;

            bool hasDlls = TunnelDll.ValidateDlls() == null;
            bool hasWg   = MainWindow.FindWireGuardExe() != null;
            var  lines   = new System.Text.StringBuilder();
            bool allOk   = true;

            if (mode != AppMode.Companion)
            {
                if (hasDlls) lines.AppendLine("✓  " + Lang.T("WizardDllOk"));
                else         { lines.AppendLine("⚠  " + Lang.T("WizardDllMissing")); allOk = false; }
            }
            if (mode != AppMode.Standalone)
            {
                if (hasWg) lines.AppendLine("✓  " + Lang.T("WizardWgBody"));
                else
                {
                    lines.AppendLine("⚠  " + Lang.T("WizardWgMissing"));
                    lines.AppendLine("    ↳ " + Lang.T("WgDownloadHint"));
                    allOk = false;
                }
            }
            ModeStatusLabel.Text       = lines.ToString().TrimEnd();
            ModeStatusLabel.Foreground = (SolidColorBrush)FindResource(allOk ? "Green" : "Red");
        }

        // ── Navigation ────────────────────────────────────────────────────────
        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_step < TotalSteps - 1)
            {
                ShowStep(_step + 1);
            }
            else
            {
                ApplyAndClose();
            }
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (_step > 0) ShowStep(_step - 1);
        }

        private void Skip_Click(object sender, RoutedEventArgs e)
        {
            // Skip discards all pending changes
            Close();
        }

        // ── Apply all deferred changes at once on Finish ──────────────────────
        private void ApplyAndClose()
        {
            var cfg = _main.GetConfig();

            // Language — already shown live in the UI but commit the code now
            if (!string.IsNullOrEmpty(_pendingLang))
                AppConfig.SaveLanguage(_pendingLang);

            // Mode
            AppMode newMode = AppMode.Standalone;
            if (WizModeCompanion.IsChecked == true) newMode = AppMode.Companion;
            else if (WizModeMixed.IsChecked == true) newMode = AppMode.Mixed;
            if (cfg.Mode != newMode)
            {
                _main.SetMode(newMode);
                _main.ApplyLocalTunnelModePublic();
            }

            // Manual mode
            bool newManual = WizManualToggle.IsChecked == true;
            if (cfg.ManualMode != newManual)
            {
                cfg.ManualMode = newManual;
                _main.ApplyManualMode();
            }

            _main.SaveConfigPublic();
            Close();
        }

        // ── Language — show change live but defer save to Finish ─────────────
        private void WizLang_Changed(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (WizLangPicker.SelectedItem is LangItem item)
            {
                _pendingLang = item.Code;
                // Apply display immediately so the wizard UI updates
                Lang.Instance.Load(item.Code);
                // Do NOT call AppConfig.SaveLanguage here — deferred to Finish
            }
        }

        // ── Mode — update status display but don't apply to config yet ────────
        private void WizMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_step == 2) RefreshModeStatus();
        }

        // ── Window chrome ─────────────────────────────────────────────────────
        private void TitleBar_MouseDown(object sender,
            System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        }
    }
}

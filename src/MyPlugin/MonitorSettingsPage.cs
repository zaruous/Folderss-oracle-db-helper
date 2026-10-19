using Folderss.Plugins;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace MyPlugin
{
    /// <summary>
    /// 설정 창의 "성능 모니터" 탭: 일일 점검 결과 기록 켬·끔, 보관 기간(일), 이력 파일 위치와 [이력 지우기…].
    /// 켬·끔과 보관 기간은 설정 창 [저장]에서 쓰고, [이력 지우기…]는 확인 뒤 바로 지운다(되돌릴 수 없음 — 사용자 항목·임계값은 남는다).
    /// </summary>
    public sealed class MonitorSettingsPage : IPluginSettingsPage
    {
        private readonly IPluginManager _manager;
        private CheckBox _record;
        private TextBox _retention;

        public MonitorSettingsPage(IPluginManager manager)
        {
            _manager = manager;
        }

        public string Title { get { return "성능 모니터"; } }

        public FrameworkElement CreateView()
        {
            var root = new StackPanel { Margin = new Thickness(8) };
            var head = Theme.Text("일일 점검 이력");
            head.FontWeight = FontWeights.SemiBold;
            root.Children.Add(head);

            _record = DialogKit.Check("점검할 때마다 결과(상태·값·행 수)를 기록 — 다음 점검에서 '직전 대비'를 보입니다");
            _record.IsChecked = CheckLogic.IsRecording(Get(CheckLogic.RecordSettingKey));
            _record.Margin = new Thickness(0, 8, 0, 0);
            AutomationProperties.SetAutomationId(_record, "monitor-settings-record");
            root.Children.Add(_record);
            var note = Theme.Secondary("발견 행(객체·사용자 이름 등 DB 내용)은 기록하지 않습니다. 엑셀로 저장한 파일에만 남습니다.");
            note.TextWrapping = TextWrapping.Wrap;
            note.Margin = new Thickness(20, 2, 0, 0);
            root.Children.Add(note);

            var days = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            var label = Theme.Text("보관 기간", Theme.SecondaryText);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(0, 0, 8, 0);
            days.Children.Add(label);
            bool valid;
            _retention = new TextBox { Width = 70, Text = CheckLogic.RetentionDays(Get(CheckLogic.RetentionSettingKey), out valid).ToString(CultureInfo.InvariantCulture), HorizontalContentAlignment = HorizontalAlignment.Right };
            AutomationProperties.SetName(_retention, "보관 기간(일)");
            AutomationProperties.SetAutomationId(_retention, "monitor-settings-retention");
            days.Children.Add(_retention);
            var unit = Theme.Text("일 (" + CheckLogic.MinRetentionDays.ToString(CultureInfo.InvariantCulture) + "~" + CheckLogic.MaxRetentionDays.ToString(CultureInfo.InvariantCulture) + ") — 점검할 때 지난 기록을 지웁니다", Theme.SecondaryText);
            unit.VerticalAlignment = VerticalAlignment.Center;
            unit.Margin = new Thickness(8, 0, 0, 0);
            days.Children.Add(unit);
            root.Children.Add(days);

            var path = Theme.Secondary("");
            path.TextWrapping = TextWrapping.Wrap;
            path.Margin = new Thickness(0, 14, 0, 0);
            AutomationProperties.SetAutomationId(path, "monitor-settings-path");
            root.Children.Add(path);
            var clear = new Button { Content = "이력 지우기…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(12, 3, 12, 3) };
            AutomationProperties.SetAutomationId(clear, "monitor-settings-clear");
            clear.Click += (s, e) =>
            {
                var owner = Window.GetWindow(root);
                if (!Dialogs.Confirm(owner, Title, "일일 점검 기록을 모두 지웁니다. 다음 점검에는 '직전 대비'가 없습니다. 사용자 점검 항목과 켬·끔·임계값은 남습니다.", "지우기", true))
                    return;
                try
                {
                    var store = MonitorStore.Open(_manager.DataDirectory);
                    var removed = store.Purge(DateTime.UtcNow.AddYears(1));
                    ShowPath(path);
                    Dialogs.Show(owner, Title, "점검 기록 " + removed.ToString(CultureInfo.InvariantCulture) + "건을 지웠습니다.", false);
                }
                catch (Exception ex)
                {
                    Dialogs.Show(owner, Title, "이력을 지우지 못했습니다: " + ex.Message, true);
                }
            };
            root.Children.Add(clear);
            ShowPath(path);
            return root;
        }

        private void ShowPath(TextBlock text)
        {
            try
            {
                var file = MonitorStore.PathIn(_manager.DataDirectory);
                if (!System.IO.File.Exists(file))
                {
                    text.Text = "이력 파일: " + file + " (아직 없음)";
                    return;
                }
                var store = MonitorStore.Open(_manager.DataDirectory);
                text.Text = "이력 파일: " + file + " · 점검 기록 " + store.RunCount().ToString("N0", CultureInfo.InvariantCulture) + "건" + (store.ReadOnly ? " (더 새 버전의 파일 — 읽기만)" : "");
            }
            catch (Exception ex)
            {
                text.Text = "이력 파일을 열 수 없습니다: " + ex.Message;
            }
        }

        /// <summary>보관 기간이 숫자가 아니거나 범위 밖이면 예외(설정 창이 저장 실패로 알린다).</summary>
        public void Save()
        {
            if (_record == null)
                return;
            var text = (_retention.Text ?? "").Trim();
            int days;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out days) || days < CheckLogic.MinRetentionDays || days > CheckLogic.MaxRetentionDays)
                throw new InvalidOperationException("성능 모니터 보관 기간은 " + CheckLogic.MinRetentionDays.ToString(CultureInfo.InvariantCulture) + "~"
                    + CheckLogic.MaxRetentionDays.ToString(CultureInfo.InvariantCulture) + "일 사이의 숫자여야 합니다: " + text);
            _manager.SetSetting(CheckLogic.RecordSettingKey, _record.IsChecked == true ? "true" : "false");
            _manager.SetSetting(CheckLogic.RetentionSettingKey, days.ToString(CultureInfo.InvariantCulture));
        }

        private string Get(string key)
        {
            try
            {
                return _manager.GetSetting(key);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}

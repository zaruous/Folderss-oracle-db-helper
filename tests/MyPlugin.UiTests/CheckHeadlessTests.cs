using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using MyPlugin.UiTests.Headless;
using Xunit;
using Xunit.Abstractions;

namespace MyPlugin.UiTests
{
    /// <summary>
    /// 일일 점검(8-3)을 헤드리스로: 실제 Oracle(FMON_UI_ORACLE, SELECT_CATALOG_ROLE 계정)에 점검을 돌리고 상태·건너뜀 이유·직전 대비,
    /// history.db 기록(플러그인 로드 컨텍스트 안에서 e_sqlite3가 불러졌는지), 엑셀 보고서, 항목 관리(켬·끔·임계값·사용자 항목 시험 실행·내보내기),
    /// 설정 탭(기록 끔·보관 기간 검사·이력 지우기)을 확인한다.
    /// </summary>
    public class CheckHeadlessTests
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan RunWait = TimeSpan.FromSeconds(180);
        private readonly ITestOutputHelper _out;

        public CheckHeadlessTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static void Seed(PluginUnderTest plugin, bool withDown)
        {
            var seeds = new List<SeedConnection>
            {
                SeedConnection.Oracle("XE-dba", HeadlessEnvironment.Oracle, HeadlessEnvironment.OracleUser, HeadlessEnvironment.OraclePassword, "green"),
                new SeedConnection { Name = "PG16", Provider = "postgres", Port = 55432, Service = "postgres", User = "postgres", Password = "x" }
            };
            if (withDown)
                seeds.Insert(1, SeedConnection.Oracle("XE-down", "localhost:1599/xe", "fmon_dba", "x", "red"));
            plugin.Manager.SeedConnections(seeds.ToArray());
        }

        private static string HistoryPath(PluginUnderTest plugin)
        {
            return Path.Combine(plugin.Manager.DataDirectory, "monitor-history", "history.db");
        }

        private static long Count(PluginUnderTest plugin, string sql)
        {
            using (var connection = new SqliteConnection("Data Source=" + HistoryPath(plugin) + ";Mode=ReadOnly;Pooling=False"))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    return Convert.ToInt64(command.ExecuteScalar());
                }
            }
        }

        private static async Task<Window> OpenChecks(UiThread ui, Window main)
        {
            ui.Run(() => Ui.Menu(main, "도구", "일일 점검"));
            return await ui.WaitFor(() => Ui.Window("일일 점검"), Wait, "일일 점검 창");
        }

        /// <summary>[점검 실행]을 누르고 다시 눌릴 수 있을 때(모든 DB 끝)까지 기다린다.</summary>
        private static async Task RunChecks(UiThread ui, Window window)
        {
            ui.Run(() => Ui.Click(Ui.Button(window, "점검 실행")));
            await ui.WaitUntil(() => !Ui.Button(window, "점검 실행").IsEnabled, Wait, "점검 시작");
            await ui.WaitUntil(() => Ui.Button(window, "점검 실행").IsEnabled, RunWait, "점검 끝");
        }

        private static Dictionary<string, string> Rows(Window window, string db)
        {
            var prefix = "check-row:" + db + ":";
            return Ui.FindAll<FrameworkElement>(window, e => (AutomationProperties.GetAutomationId(e) ?? "").StartsWith(prefix, StringComparison.Ordinal))
                .ToDictionary(e => AutomationProperties.GetAutomationId(e).Substring(prefix.Length), e => AutomationProperties.GetName(e) + " ¦ " + AutomationProperties.GetHelpText(e));
        }

        private static string Text(Window window, string automationId)
        {
            var block = Ui.ById<TextBlock>(window, automationId);
            return block == null ? null : block.Text;
        }

        [UiFact(requiresOracle: true)]
        public async Task Checks_Run_Record_CompareWithPrevious_SaveExcel()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            Seed(plugin, true);
            // 처음 여는 것처럼(고른 DB 기억 없음) — 저장된 비밀번호가 있는 접속이 골라져 있어야 한다
            plugin.Manager.SetSetting("monitor.check.dbs", null);
            var main = ui.Run(() => plugin.OpenView());
            try
            {
                var window = await OpenChecks(ui, main);
                // 저장된 비밀번호가 있는 Oracle 접속은 처음부터 골라져 있고, PostgreSQL은 지원 예정이라 꺼져 있다
                ui.Run(() =>
                {
                    Assert.True(Ui.ById<CheckBox>(window, "check-db:XE-dba").IsChecked == true);
                    Assert.True(Ui.ById<CheckBox>(window, "check-db:XE-down").IsChecked == true);
                    Assert.False(Ui.ById<CheckBox>(window, "check-db:PG16").IsEnabled);
                    Ui.Toggle(Ui.ById<CheckBox>(window, "check-db:PG16"), true);
                });
                var runsBefore = File.Exists(HistoryPath(plugin)) ? Count(plugin, "SELECT COUNT(*) FROM check_run") : 0;

                await RunChecks(ui, window);
                var rows = ui.Run(() => Rows(window, "XE-dba"));
                foreach (var row in rows)
                    _out.WriteLine(row.Key + " = " + row.Value);
                _out.WriteLine("요약: " + ui.Run(() => Text(window, "check-summary")));
                _out.WriteLine("XE-down: " + ui.Run(() => Text(window, "check-db-state:XE-down")));
                Assert.Equal(17, rows.Count);
                Assert.DoesNotContain(rows, r => r.Value.Contains(" 대기 ¦"));
                // 11g: 경고 로그 뷰는 SELECT_CATALOG_ROLE로 안 보인다 → 건너뜀(권한) + 필요 권한 안내
                Assert.StartsWith("경고 로그 ORA- 오류(24시간) 건너뜀", rows["ora.alert_log"]);
                Assert.Contains("필요 권한: V$DIAG_ALERT_EXT", rows["ora.alert_log"]);
                Assert.StartsWith("인스턴스 상태·가동 시간 정상", rows["ora.instance"]);
                Assert.DoesNotContain(rows, r => r.Value.Contains(" 오류 ¦"));
                Assert.Contains("점검 못 함: ORA-12541", ui.Run(() => Text(window, "check-db-state:XE-down")));
                Assert.Null(ui.Run(() => Ui.ById<FrameworkElement>(window, "check-db-state:PG16")));

                // history.db: 플러그인 컨텍스트가 e_sqlite3를 불렀고, 접속된 DB만 한 번 기록(발견 행 없이 상태·값·행 수)
                // (publish(win-x64)는 네이티브 DLL을 플러그인 폴더 바로 아래에 둔다 — P/Invoke 진입점마다 불려 같은 경로가 여러 번 남는다)
                var natives = plugin.Context.NativeLoads.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                _out.WriteLine("네이티브: " + string.Join(", ", natives));
                Assert.Contains(Path.Combine(plugin.PluginDirectory, "e_sqlite3.dll"), natives, StringComparer.OrdinalIgnoreCase);
                Assert.Equal(runsBefore + 1, Count(plugin, "SELECT COUNT(*) FROM check_run"));
                var lastRun = Count(plugin, "SELECT MAX(id) FROM check_run");
                Assert.Equal(17, Count(plugin, "SELECT COUNT(*) FROM check_result WHERE run_id = " + lastRun));
                Assert.Equal(1, Count(plugin, "SELECT COUNT(*) FROM check_result WHERE run_id = " + lastRun + " AND item_id = 'ora.alert_log' AND status = 'skip' AND reason = 'privilege'"));
                Assert.Equal(1, Count(plugin, "SELECT COUNT(*) FROM db WHERE label LIKE 'XE (DBID %'"));
                // 결과 표에는 발견 행을 담을 열이 없다(run_id·item_id·status·value·row_count·reason)
                Assert.Equal(6, Count(plugin, "SELECT COUNT(*) FROM pragma_table_info('check_result')"));

                // 항목을 고르면 상세: 판정 기준·발견 행
                ui.Run(() =>
                {
                    var row = Ui.ById<Border>(window, "check-row:XE-dba:ora.tablespace");
                    row.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                    {
                        RoutedEvent = UIElement.MouseLeftButtonUpEvent
                    });
                });
                await ui.WaitUntil(() => (Text(window, "check-detail-title") ?? "").Contains("테이블스페이스 사용률"), Wait, "상세 제목");
                var facts = ui.Run(() => Text(window, "check-detail-facts"));
                _out.WriteLine("상세: " + facts + " / " + ui.Run(() => Text(window, "check-findings-text")));
                Assert.Contains("판정: 사용률(%) ≥ 85 주의 · ≥ 95 위험", facts);
                Assert.Contains("직전 기록 없음", facts);
                Assert.True(ui.Run(() => Ui.Find<ListView>(window, l => AutomationProperties.GetName(l) == "발견 행").Items.Count) > 0);
                _out.WriteLine("캡처: " + ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "checks-1")));

                // 두 번째 실행: 직전 대비(같음)와 직전 시각
                await RunChecks(ui, window);
                var second = ui.Run(() => Rows(window, "XE-dba"));
                Assert.Contains(" · 직전 ", ui.Run(() => Text(window, "check-db-state:XE-dba")));
                Assert.Contains("같음", ui.Run(() => Ui.Texts(window)));
                Assert.Equal(runsBefore + 2, Count(plugin, "SELECT COUNT(*) FROM check_run"));
                Assert.Equal(17, second.Count);
                _out.WriteLine("캡처: " + ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "checks-2-compare")));

                // 엑셀로 저장 → 기본 양식(요약 + DB마다 시트)
                var xlsx = Path.Combine(plugin.ShotFolder, "daily-check.xlsx");
                ui.Post(() => Ui.Click(Ui.Button(window, "엑셀로 저장…")));
                _out.WriteLine("대화상자: " + await NativeDialogs.SaveAs(ui, xlsx, Wait));
                await ui.WaitUntil(() => File.Exists(xlsx) && Ui.Texts(window).Any(t => t.StartsWith("저장했습니다: ", StringComparison.Ordinal)), Wait, "엑셀 저장");
                using (var book = new XLWorkbook(xlsx))
                {
                    Assert.Equal(new[] { "요약", "XE-dba", "XE-down" }, book.Worksheets.Select(w => w.Name));
                    var summary = book.Worksheet("요약").CellsUsed().Select(c => c.GetString()).ToList();
                    Assert.Contains(summary, t => t.StartsWith("접속 실패 — ORA-12541", StringComparison.Ordinal));
                    var sheet = book.Worksheet("XE-dba");
                    var cells = sheet.CellsUsed().Select(c => c.GetString()).ToList();
                    Assert.Contains(cells, t => t.Contains("직전 점검"));
                    var alert = sheet.CellsUsed().First(c => c.GetString() == "경고 로그 ORA- 오류(24시간)").Address.RowNumber;
                    Assert.Equal("건너뜀", sheet.Cell(alert, 5).GetString());
                    Assert.Contains("필요 권한", sheet.Cell(alert, 9).GetString());
                    Assert.DoesNotContain(cells, t => t.Contains("${") || t.StartsWith("#", StringComparison.Ordinal));
                }
                Assert.Equal(plugin.ShotFolder, plugin.Manager.GetSetting("monitor.check.folder"));
                ui.Run(() => window.Close());
            }
            finally
            {
                ui.Run(() => main.Close());
            }
            Assert.Empty(ui.TakeErrors());
        }

        [UiFact(requiresOracle: true)]
        public async Task Items_DisableOverrideAddUserItem_TestRun_Export_ThenRun()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            Seed(plugin, false);
            plugin.Manager.SetSetting("monitor.check.dbs", null);
            var main = ui.Run(() => plugin.OpenView());
            try
            {
                var window = await OpenChecks(ui, main);
                ui.Post(() => Ui.Click(Ui.Button(window, "항목 관리…")));
                var items = await ui.WaitFor(() => Ui.Window("점검 항목 관리"), Wait, "항목 관리 창");

                // 기본 항목: 로그 모드 끄기, 테이블스페이스 주의 문턱 70
                ui.Run(() => Ui.ClickToggle(Ui.ById<CheckBox>(items, "check-item-enabled:ora.log_mode")));
                ui.Run(() =>
                {
                    var warn = Ui.ById<TextBox>(items, "check-item-warn:ora.tablespace");
                    warn.Text = "70";
                    Ui.PressEnter(warn);
                });
                await ui.WaitUntil(() => (Text(items, "check-items-status") ?? "").Contains("주의 70"), Wait, "임계값 저장");
                Assert.Equal(1, Count(plugin, "SELECT COUNT(*) FROM check_override WHERE db_key = '' AND item_id = 'ora.log_mode' AND enabled = 0"));
                Assert.Equal(1, Count(plugin, "SELECT COUNT(*) FROM check_override WHERE db_key = '' AND item_id = 'ora.tablespace' AND warn = 70 AND crit IS NULL"));

                // 사용자 항목 추가: 시험 실행으로 결과 열을 받아 보고 저장
                ui.Post(() => Ui.Click(Ui.Button(items, "추가…")));
                var edit = await ui.WaitFor(() => Ui.Window("점검 항목 추가"), Wait, "항목 추가 창");
                ui.Run(() =>
                {
                    Ui.ById<TextBox>(edit, "check-edit-title").Text = "FMON 계정";
                    Ui.ById<TextBox>(edit, "check-edit-sql").Text = "SELECT USERNAME AS \"사용자\", ACCOUNT_STATUS AS \"상태\" FROM DBA_USERS WHERE USERNAME LIKE 'FMON%' ORDER BY 1";
                    Assert.Equal("XE-dba", Ui.ContentText(((ComboBoxItem)Ui.ById<ComboBox>(edit, "check-edit-testdb").SelectedItem).Content));
                    Ui.Click(Ui.ById<Button>(edit, "check-edit-test"));
                });
                await ui.WaitUntil(() => (Text(edit, "check-edit-test-result") ?? "").StartsWith("주의 — ", StringComparison.Ordinal), Wait, "시험 실행 결과");
                var tested = ui.Run(() => Text(edit, "check-edit-test-result"));
                _out.WriteLine("시험 실행: " + tested);
                Assert.Contains("결과 ", tested);
                Assert.Equal(new[] { "사용자", "상태" }, ui.Run(() => Ui.ById<ComboBox>(edit, "check-edit-column").Items.Cast<object>().Select(o => o.ToString()).ToArray()));
                _out.WriteLine("캡처: " + ui.Run(() => Ui.Snap(edit, plugin.ShotFolder, "checks-3-edit")));
                // 임계값 규칙은 판정 열을 골라야 저장된다(열은 손으로 치지 않고 시험 실행 결과에서 고른다)
                ui.Run(() =>
                {
                    Ui.Choose(Ui.ById<ComboBox>(edit, "check-edit-rule"), "수 열의 최댓값으로 (임계값)");
                    Ui.Click(Ui.ById<Button>(edit, "check-edit-save"));
                });
                await ui.WaitUntil(() => (Text(edit, "check-edit-errors") ?? "").Contains("판정 열을 고르세요"), Wait, "판정 열 안내");
                ui.Run(() => Ui.Choose(Ui.ById<ComboBox>(edit, "check-edit-rule"), "행이 있으면 주의"));
                // 잘못된 SQL은 저장하지 않는다(자동화 Invoke는 디스패처에 맡겨져 나중에 돈다 — 결과는 기다려 본다)
                var keep = ui.Run(() => Ui.ById<TextBox>(edit, "check-edit-sql").Text);
                ui.Run(() =>
                {
                    Ui.ById<TextBox>(edit, "check-edit-sql").Text = "DELETE FROM DUAL";
                    Ui.Click(Ui.ById<Button>(edit, "check-edit-save"));
                });
                await ui.WaitUntil(() => (Text(edit, "check-edit-errors") ?? "").Contains("SELECT(WITH) 문이어야"), Wait, "SELECT만 된다는 오류");
                Assert.NotNull(ui.Run(() => Ui.Window("점검 항목 추가")));
                ui.Run(() =>
                {
                    Ui.ById<TextBox>(edit, "check-edit-sql").Text = keep;
                    Ui.Click(Ui.ById<Button>(edit, "check-edit-save"));
                });
                await ui.WaitUntil(() => Ui.Window("점검 항목 추가") == null, Wait, "추가 창 닫힘");
                await ui.WaitUntil(() => Ui.ById<ListBoxItem>(items, "check-user-item:FMON 계정") != null, Wait, "사용자 항목 목록");
                Assert.Equal(1, Count(plugin, "SELECT COUNT(*) FROM check_item WHERE title = 'FMON 계정' AND kind = 'oracle' AND id LIKE 'user.%'"));

                // 내보내기(JSON)
                var json = Path.Combine(plugin.ShotFolder, "checks.json");
                ui.Post(() => Ui.Click(Ui.Button(items, "내보내기…")));
                await NativeDialogs.SaveAs(ui, json, Wait);
                await ui.WaitUntil(() => (Text(items, "check-items-status") ?? "").StartsWith("1개를 내보냈습니다", StringComparison.Ordinal), Wait, "내보내기");
                var exported = File.ReadAllText(json);
                Assert.Contains("\"format\": \"folderss-db-helper-checks\"", exported);
                Assert.Contains("FMON 계정", exported);
                _out.WriteLine("캡처: " + ui.Run(() => Ui.Snap(items, plugin.ShotFolder, "checks-4-items")));
                ui.Run(() => Ui.Click(Ui.Button(items, "닫기")));
                await ui.WaitUntil(() => Ui.Window("점검 항목 관리") == null, Wait, "항목 관리 닫힘");

                // 다시 점검: 끈 항목은 건너뜀, 사용자 항목은 주의, 덮어쓴 문턱이 판정에 쓰인다
                await RunChecks(ui, window);
                var rows = ui.Run(() => Rows(window, "XE-dba"));
                foreach (var row in rows)
                    _out.WriteLine(row.Key + " = " + row.Value);
                Assert.Equal(18, rows.Count);
                Assert.StartsWith("아카이브 로그 모드 건너뜀", rows["ora.log_mode"]);
                Assert.Contains("꺼 둔 항목", rows["ora.log_mode"]);
                var user = rows.Single(r => r.Key.StartsWith("user.", StringComparison.Ordinal));
                Assert.StartsWith("FMON 계정 주의", user.Value);
                ui.Run(() => Ui.ById<Border>(window, "check-row:XE-dba:ora.tablespace").RaiseEvent(
                    new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent }));
                await ui.WaitUntil(() => (Text(window, "check-detail-facts") ?? "").Contains("판정: 사용률(%) ≥ 70 주의 · ≥ 95 위험"), Wait, "덮어쓴 문턱");
                _out.WriteLine("캡처: " + ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "checks-5-user-item")));

                // 원래대로(다른 시험에 영향 없게): 로그 모드 켜기, 문턱 비우기, 사용자 항목 지우기
                ui.Post(() => Ui.Click(Ui.Button(window, "항목 관리…")));
                items = await ui.WaitFor(() => Ui.Window("점검 항목 관리"), Wait, "항목 관리 창(정리)");
                ui.Run(() =>
                {
                    Ui.ClickToggle(Ui.ById<CheckBox>(items, "check-item-enabled:ora.log_mode"));
                    var warn = Ui.ById<TextBox>(items, "check-item-warn:ora.tablespace");
                    warn.Text = "";
                    Ui.PressEnter(warn);
                    var list = Ui.Find<ListBox>(items, l => AutomationProperties.GetName(l) == "사용자 항목");
                    list.SelectedItem = Ui.ById<ListBoxItem>(items, "check-user-item:FMON 계정");
                });
                ui.Post(() => Ui.Click(Ui.Button(items, "지우기")));
                var confirm = await ui.WaitFor(() => Ui.Windows().FirstOrDefault(w => w.Title == "점검 항목 관리" && w != items), Wait, "지우기 확인");
                ui.Run(() => Ui.Click(Ui.Button(confirm, "지우기")));
                await ui.WaitUntil(() => (Text(items, "check-items-status") ?? "").StartsWith("지웠습니다", StringComparison.Ordinal), Wait, "지우기");
                ui.Run(() => Ui.Click(Ui.Button(items, "닫기")));
                Assert.Equal(0, Count(plugin, "SELECT COUNT(*) FROM check_item"));
                Assert.Equal(0, Count(plugin, "SELECT COUNT(*) FROM check_override"));
                ui.Run(() => window.Close());
            }
            finally
            {
                ui.Run(() => main.Close());
            }
            Assert.Empty(ui.TakeErrors());
        }

        [UiFact]
        public async Task SettingsPage_RecordToggle_RetentionCheck_ClearHistory()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var page = plugin.Manager.SettingsPages.Single(p => p.Title == "성능 모니터");
            var host = ui.Run(() =>
            {
                var window = new Window { Title = "설정", Content = page.CreateView(), Width = 720, Height = 480, ShowActivated = false, ShowInTaskbar = false };
                window.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
                window.Show();
                return window;
            });
            try
            {
                ui.Run(() =>
                {
                    Assert.True(Ui.ById<CheckBox>(host, "monitor-settings-record").IsChecked == true);
                    Assert.Equal("90", Ui.ById<TextBox>(host, "monitor-settings-retention").Text);
                    _out.WriteLine(Text(host, "monitor-settings-path"));
                    Assert.Contains(Path.Combine("monitor-history", "history.db"), Text(host, "monitor-settings-path"));
                });
                // 범위 밖 보관 기간은 저장 실패(설정 창이 알린다)
                ui.Run(() =>
                {
                    Ui.ById<TextBox>(host, "monitor-settings-retention").Text = "3";
                    var error = Assert.Throws<InvalidOperationException>(() => page.Save());
                    Assert.Contains("7~3650", error.Message);
                    Ui.ById<TextBox>(host, "monitor-settings-retention").Text = "30";
                    Ui.ClickToggle(Ui.ById<CheckBox>(host, "monitor-settings-record"));
                    page.Save();
                });
                Assert.Equal("false", plugin.Manager.GetSetting("monitor.check.record"));
                Assert.Equal("30", plugin.Manager.GetSetting("monitor.history.retentionDays"));
                _out.WriteLine("캡처: " + ui.Run(() => Ui.Snap(host, plugin.ShotFolder, "checks-6-settings")));

                // 이력 지우기: 위험 확인(체크해야 눌림) → 지운 건수 안내
                ui.Post(() => Ui.Click(Ui.ById<Button>(host, "monitor-settings-clear")));
                var confirm = await ui.WaitFor(() => Ui.Windows().FirstOrDefault(w => w.Title == "성능 모니터" && w.IsVisible), Wait, "지우기 확인");
                ui.Run(() =>
                {
                    var yes = Ui.Button(confirm, "지우기");
                    Assert.False(yes.IsEnabled);
                    Ui.Toggle(Ui.Find<CheckBox>(confirm), true);
                    Assert.True(yes.IsEnabled);
                    Ui.Click(yes);
                });
                var done = await ui.WaitFor(() => Ui.Windows().FirstOrDefault(w => w.Title == "성능 모니터" && w.IsVisible && Ui.Texts(w).Any(t => t.StartsWith("점검 기록 ", StringComparison.Ordinal))), Wait, "지운 건수 안내");
                _out.WriteLine(string.Join(" ¦ ", ui.Run(() => Ui.Texts(done))));
                ui.Run(() => Ui.Click(Ui.Button(done, "확인")));
                if (File.Exists(HistoryPath(plugin)))
                    Assert.Equal(0, Count(plugin, "SELECT COUNT(*) FROM check_run"));

                // 원래대로
                ui.Run(() =>
                {
                    Ui.ClickToggle(Ui.ById<CheckBox>(host, "monitor-settings-record"));
                    Ui.ById<TextBox>(host, "monitor-settings-retention").Text = "90";
                    page.Save();
                });
                Assert.Equal("true", plugin.Manager.GetSetting("monitor.check.record"));
            }
            finally
            {
                ui.Run(() => host.Close());
            }
            Assert.Empty(ui.TakeErrors());
        }
    }
}

using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MyPlugin.UiTests.Headless;
using Xunit;
using Xunit.Abstractions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MyPlugin.UiTests
{
    /// <summary>전체 DB 상태(8-2)를 헤드리스로: 플러그인 창 → 도구 메뉴 → 타일 상태·오른쪽 메뉴·고정, 캡처.</summary>
    public class OverviewHeadlessTests
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
        private readonly ITestOutputHelper _out;

        public OverviewHeadlessTests(ITestOutputHelper output)
        {
            _out = output;
        }

        [UiFact]
        public async Task Overview_TileStates_ContextMenu_Pin()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var seeds = new System.Collections.Generic.List<SeedConnection>
            {
                SeedConnection.Oracle("XE-down", "localhost:1599/xe", "fmon_dba", "x", "red"),
                SeedConnection.Oracle("XE-nopw", "localhost:1521/xe", "fmon_dba", null),
                new SeedConnection { Name = "PG16", Provider = "postgres", Port = 55432, Service = "postgres", User = "postgres", Password = "x", Color = "green" }
            };
            if (!string.IsNullOrEmpty(HeadlessEnvironment.Oracle))
                seeds.Add(SeedConnection.Oracle("XE-dba", HeadlessEnvironment.Oracle, HeadlessEnvironment.OracleUser, HeadlessEnvironment.OraclePassword, "green"));
            plugin.Manager.SeedConnections(seeds.ToArray());

            var main = ui.Run(() => plugin.OpenView());
            try
            {
                ui.Run(() => Ui.Menu(main, "도구", "전체 DB 상태"));
                var overview = await ui.WaitFor(() => Ui.Window("전체 DB 상태"), Wait, "전체 DB 상태 창");
                // 닫힌 포트는 ODP.NET 재시도로 몇 초 — 실패 타일이 나올 때까지
                await ui.WaitUntil(() => Ui.Texts(overview).Contains("접속 실패"), Wait, "XE-down 접속 실패 타일");
                if (seeds.Any(s => s.Name == "XE-dba"))
                    await ui.WaitUntil(() => Ui.Texts(overview).Any(t => t.StartsWith("활성 ", StringComparison.Ordinal)), Wait, "XE-dba 지표");
                var texts = ui.Run(() => Ui.Texts(overview));
                _out.WriteLine(string.Join(" ¦ ", texts));
                Assert.Contains("비밀번호 필요", texts);
                Assert.Contains("지원 예정", texts);
                Assert.Contains(texts, t => t.StartsWith("ORA-12541", StringComparison.Ordinal));
                // 심각도순: 접속 실패가 비밀번호 필요보다 앞
                Assert.True(texts.IndexOf("XE-down") < texts.IndexOf("XE-nopw"));
                _out.WriteLine("캡처: " + ui.Run(() => Ui.Snap(overview, plugin.ShotFolder, "overview")));

                // 타일 오른쪽 메뉴 → 맨 앞에 고정 → 순서·설정
                var menu = ui.Run(() => Ui.ContextMenu(TileOf(overview, "XE-nopw"), "맨 앞에 고정"));
                _out.WriteLine("타일 메뉴: " + string.Join(" · ", menu));
                Assert.Equal(new[] { "성능 모니터 열기", "잠금 트리 열기", "지금 새로 고침", "맨 앞에 고정", "이 화면에서 빼기" }, menu);
                await ui.WaitUntil(() => Ui.Texts(overview).Any(t => t.EndsWith("XE-nopw", StringComparison.Ordinal) && t.StartsWith("📌", StringComparison.Ordinal)), Wait, "고정 표시");
                var order = ui.Run(() => Ui.Texts(overview));
                Assert.True(order.FindIndex(t => t.EndsWith("XE-nopw", StringComparison.Ordinal)) < order.IndexOf("XE-down"));
                Assert.False(string.IsNullOrEmpty(plugin.Manager.GetSetting("monitor.overview.pinned")));
                ui.Run(() => Ui.ContextMenu(TileOf(overview, "XE-nopw"), "고정 풀기"));
                await ui.WaitUntil(() => string.IsNullOrEmpty(plugin.Manager.GetSetting("monitor.overview.pinned")), Wait, "고정 풀기 저장");
                ui.Run(() => overview.Close());
            }
            finally
            {
                ui.Run(() => main.Close());
            }
            Assert.Empty(ui.TakeErrors());
        }

        /// <summary>타일: 이름 글이 name으로 끝나는 TextBlock을 담은, 오른쪽 메뉴가 있는 요소.</summary>
        internal static FrameworkElement TileOf(Window window, string name)
        {
            var label = Ui.Find<TextBlock>(window, t => t.IsVisible && (t.Text ?? "").EndsWith(name, StringComparison.Ordinal) && t.FontSize >= 14);
            DependencyObject node = label;
            while (node != null)
            {
                var element = node as FrameworkElement;
                if (element != null && element.ContextMenu != null)
                    return element;
                node = System.Windows.Media.VisualTreeHelper.GetParent(node);
            }
            throw new InvalidOperationException("타일 '" + name + "'을 찾지 못했습니다.");
        }
    }
}

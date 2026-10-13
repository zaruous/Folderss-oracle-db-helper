using System.Collections.Generic;
using System.Linq;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>ShellLogic: 툴바 문장(고른 DB·모드 표시), 연결·끊기 메시지, 가져올 행, 접속 목록 다시 읽기.</summary>
    public class ShellLogicTests
    {
        private static OracleConnectionProfile Profile(string id = "dev", string name = "개발", bool readOnly = false)
        {
            return new OracleConnectionProfile
            {
                Id = id, Name = name, Host = "dev-db", Port = 1521, ServiceName = "ORCLPDB1", UserId = "scott", ReadOnly = readOnly
            };
        }

        [Fact]
        public void Address_UserHostPortService_Trimmed()
        {
            var profile = Profile();
            profile.Host = " dev-db ";

            Assert.Equal("scott@dev-db:1521/ORCLPDB1", ShellLogic.Address(profile));
            Assert.Equal("", ShellLogic.Address(null));
        }

        [Fact]
        public void SelectedDbText_NameAndAddressOrHint()
        {
            Assert.Equal("개발 · scott@dev-db:1521/ORCLPDB1", ShellLogic.SelectedDbText(Profile()));
            Assert.Equal("트리에서 DB를 고르세요", ShellLogic.SelectedDbText(null));
        }

        [Fact]
        public void ModeText_EveryState()
        {
            bool danger;

            Assert.Equal("대상 DB 없음", ShellLogic.ModeText(null, null, out danger));
            Assert.False(danger);
            Assert.Equal("개발 · 자동 커밋 끔", ShellLogic.ModeText(Profile(), null, out danger));
            Assert.False(danger);
            Assert.Equal("운영 · 읽기 전용", ShellLogic.ModeText(Profile("prod", "운영", readOnly: true), null, out danger));
            Assert.False(danger);
            Assert.Equal("개발 · 커밋 대기 3행", ShellLogic.ModeText(Profile(), "커밋 대기 3행", out danger));
            Assert.True(danger);
        }

        [Fact]
        public void ModeText_ReadOnlyWithForUpdateLocks_PendingWins()
        {
            bool danger;

            var text = ShellLogic.ModeText(Profile("prod", "운영", readOnly: true), "커밋 대기 0행", out danger);

            Assert.Equal("운영 · 커밋 대기 0행", text);
            Assert.True(danger);
        }

        [Fact]
        public void ConnectedMessage_AddressAndReadOnly()
        {
            Assert.Equal("연결됨: scott@dev-db:1521/ORCLPDB1", ShellLogic.ConnectedMessage(Profile()));
            Assert.Equal("연결됨: scott@dev-db:1521/ORCLPDB1 · 읽기 전용", ShellLogic.ConnectedMessage(Profile(readOnly: true)));
        }

        [Fact]
        public void ReconnectAndBrokenMessages_MentionLostPendingOnlyWhenThereWasSome()
        {
            Assert.Equal("DB 연결이 끊겨 다시 연결합니다.", ShellLogic.ReconnectMessage(null));
            Assert.Equal("DB 연결이 끊겨 다시 연결합니다. 커밋하지 않은 변경(커밋 대기 2행)은 서버에서 이미 사라졌습니다.", ShellLogic.ReconnectMessage("커밋 대기 2행"));
            Assert.Contains("커밋 대기(행 수 모름)", ShellLogic.BrokenDisconnectMessage("커밋 대기(행 수 모름)"));
            Assert.DoesNotContain("변경", ShellLogic.BrokenDisconnectMessage(null));
        }

        [Fact]
        public void BusyAndCloseFailedMessages()
        {
            Assert.Equal("같은 DB에서 다른 실행이 진행 중입니다. 끝난 뒤 다시 커밋하세요 (DB마다 세션 1개).", ShellLogic.BusyMessage("커밋"));
            Assert.StartsWith("커밋하지 못해 변경이 저장되지 않았습니다: ORA-02091", ShellLogic.CloseFailedMessage(true, "ORA-02091"));
            Assert.StartsWith("롤백하지 못했습니다: ORA-03113", ShellLogic.CloseFailedMessage(false, "ORA-03113"));
        }

        [Theory]
        [InlineData(1000, 1000)]
        [InlineData("5000", 5000)]
        [InlineData("abc", 200)]
        [InlineData(null, 200)]
        [InlineData(0, 200)]
        [InlineData(-5, 200)]
        public void ParseFetchCount_KnownOrDefault(object value, int expected)
        {
            Assert.Equal(expected, ShellLogic.ParseFetchCount(value));
        }

        [Fact]
        public void FetchCounts_DefaultFirst()
        {
            Assert.Equal(new[] { 200, 1000, 5000 }, ShellLogic.FetchCounts);
            Assert.Equal(ShellLogic.DefaultFetchCount, ShellLogic.FetchCounts[0]);
        }

        // ---------- 열린 세션의 접속 정보 사본 ----------

        [Fact]
        public void SessionCopy_IsIndependentCopyOfEveryField()
        {
            var saved = Profile("prod", "운영", readOnly: true);
            saved.ProtectedPassword = "AQAAAN";
            saved.Color = "red";

            var copy = ShellLogic.SessionCopy(saved);
            saved.Host = "prod-db2";
            saved.Color = "green";

            Assert.NotSame(saved, copy);
            Assert.Equal(new object[] { "prod", "운영", "dev-db", 1521, "ORCLPDB1", "scott", "AQAAAN", true, "red" },
                new object[] { copy.Id, copy.Name, copy.Host, copy.Port, copy.ServiceName, copy.UserId, copy.ProtectedPassword, copy.ReadOnly, copy.Color });
            Assert.Null(ShellLogic.SessionCopy(null));
        }

        [Fact]
        public void ProfileChanged_AnySavedFieldDifferentFromSession()
        {
            var session = ShellLogic.SessionCopy(Profile());

            Assert.False(ShellLogic.ProfileChanged(ShellLogic.SessionCopy(session), session));
            var host = ShellLogic.SessionCopy(session);
            host.Host = "prod-db2";
            Assert.True(ShellLogic.ProfileChanged(host, session));
            var color = ShellLogic.SessionCopy(session);
            color.Color = "red";
            Assert.True(ShellLogic.ProfileChanged(color, session));
            var port = ShellLogic.SessionCopy(session);
            port.Port = 1522;
            Assert.True(ShellLogic.ProfileChanged(port, session));
            Assert.False(ShellLogic.ProfileChanged(null, session));
            Assert.False(ShellLogic.ProfileChanged(session, null));
        }

        [Fact]
        public void ApplySavedToSession_OnlyTurningReadOnlyOnAppliesAtOnce()
        {
            var session = ShellLogic.SessionCopy(Profile());
            var saved = ShellLogic.SessionCopy(session);
            saved.Host = "prod-db2";
            saved.ReadOnly = true;

            ShellLogic.ApplySavedToSession(session, saved);

            // 읽기 전용은 바로 켜지고 주소는 다시 연결할 때까지 그대로(변경됨 표시는 남는다)
            Assert.True(session.ReadOnly);
            Assert.Equal("dev-db", session.Host);
            Assert.True(ShellLogic.ProfileChanged(saved, session));

            var unlocked = ShellLogic.SessionCopy(session);
            unlocked.ReadOnly = false;
            ShellLogic.ApplySavedToSession(session, unlocked);
            // 읽기 전용을 끄는 것은 열린 세션에 적용하지 않는다
            Assert.True(session.ReadOnly);
            Assert.True(ShellLogic.ProfileChanged(unlocked, session));
        }

        // ---------- MergeProfiles ----------

        [Fact]
        public void MergeProfiles_LoadedOrderKept_RemovedLiveProfileAppendedAsOrphan()
        {
            var dev = Profile("dev", "개발");
            var prod = Profile("prod", "운영");
            var test = Profile("test", "검증");
            var renamedDev = Profile("dev", "개발 DB");
            var orphans = new HashSet<string>();

            var merged = ShellLogic.MergeProfiles(new[] { renamedDev, test }, new[] { dev, prod }, id => id == "prod" || id == "dev", orphans);

            Assert.Equal(new[] { "dev", "test", "prod" }, merged.Select(p => p.Id).ToArray());
            Assert.Same(renamedDev, merged[0]);
            Assert.Same(prod, merged[2]);
            Assert.Equal(new[] { "prod" }, orphans.ToArray());
        }

        [Fact]
        public void MergeProfiles_RemovedDisconnectedProfileDropped_OrphansRebuilt()
        {
            var orphans = new HashSet<string> { "old" };

            var merged = ShellLogic.MergeProfiles(new[] { Profile("dev") }, new[] { Profile("dev"), Profile("gone") }, id => false, orphans);

            Assert.Equal(new[] { "dev" }, merged.Select(p => p.Id).ToArray());
            Assert.Empty(orphans);
        }

        [Fact]
        public void MergeProfiles_OrphanBackInSavedList_NoLongerOrphan()
        {
            var orphans = new HashSet<string> { "prod" };

            var merged = ShellLogic.MergeProfiles(new[] { Profile("prod") }, new[] { Profile("prod") }, id => true, orphans);

            Assert.Single(merged);
            Assert.Empty(orphans);
        }

        [Fact]
        public void MergeProfiles_NullsBlankIdsAndDuplicatesSkipped()
        {
            var first = Profile("dev", "첫째");
            var loaded = new List<OracleConnectionProfile> { null, Profile("", "빈 Id"), first, Profile("dev", "둘째") };

            var merged = ShellLogic.MergeProfiles(loaded, null, null, null);

            Assert.Single(merged);
            Assert.Same(first, merged[0]);
            Assert.Empty(ShellLogic.MergeProfiles(null, null, null, null));
        }

        // ---------- 창 전체 단축키 ----------

        [Theory]
        [InlineData("N", true, false, false, "NewTab")]
        [InlineData("O", true, false, false, "Open")]
        [InlineData("S", true, false, false, "Save")]
        [InlineData("S", true, true, false, "SaveAs")]
        [InlineData("W", true, false, false, "CloseTab")]
        [InlineData("N", false, false, false, "None")]   // Ctrl 없음 = 글자 입력
        [InlineData("S", true, false, true, "None")]     // Alt가 섞이면 아님(AltGr 등)
        [InlineData("N", true, true, false, "None")]
        [InlineData("W", true, true, false, "None")]
        [InlineData("Enter", true, false, false, "None")] // 실행은 편집기가 처리
        [InlineData("Q", true, false, false, "None")]
        [InlineData("F5", false, false, false, "RunScript")]
        [InlineData("F5", true, false, false, "None")]
        [InlineData("F5", false, true, false, "None")]
        public void WindowShortcut_MapsCtrlKeys(string key, bool control, bool shift, bool alt, string expected)
        {
            Assert.Equal(expected, ShellLogic.WindowShortcut(key, control, shift, alt).ToString());
        }

        [Fact]
        public void ShortcutsText_ListsEveryShortcut()
        {
            foreach (var shortcut in new[] { "Ctrl+N", "Ctrl+O", "Ctrl+S", "Ctrl+Shift+S", "Ctrl+W", "Ctrl+Enter", "Ctrl+/", "Ctrl+Shift+U", "Ctrl+Shift+L", "Shift+F10", "F5", "Ctrl+F", "F3" })
                Assert.Contains(shortcut, ShellLogic.ShortcutsText);
        }
    }
}

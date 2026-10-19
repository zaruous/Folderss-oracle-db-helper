using System;
using System.Collections.Generic;

namespace MyPlugin
{
    /// <summary>
    /// 성능 모니터 창(<c>MonitorWindow</c>)이 보일 섹션을 DBMS마다 공급한다. 공급자(<see cref="IDbProvider.Monitor"/>)가 구현.
    /// - 섹션 하나 = 조회 SQL 하나. 결과 열이 그대로 표가 되므로 열 별칭이 곧 화면 머리글이다(한국어 별칭을 따옴표로).
    ///   화면은 열 이름·행 값(글자)만 알고 DBMS 고유 뷰(V$·pg_catalog·sys.dm_*)는 여기 안에만 둔다.
    /// - 바인드 변수 없음, 자료를 바꾸지 않는 SELECT만. 같은 세션의 트랜잭션 안에서 실행되므로 사용자 변경에 영향을 주면 안 된다.
    /// - 권한이 없는 계정이면 섹션마다 따로 실패한다(다른 섹션은 그대로). 어떤 권한이 필요한지는 <see cref="MonitorSection.Privilege"/>에 적어
    ///   <see cref="IDbProvider.IsPermissionDenied"/>로 판정한 오류 메시지에 함께 보인다.
    /// - 공통 섹션 id(<see cref="MonitorSections"/>)는 세 DBMS가 같은 순서로 갖는다. DBMS에만 있는 섹션은 그 뒤에 더한다.
    /// </summary>
    internal interface IMonitorProvider
    {
        /// <summary>섹션 목록(화면 순서). 이 DBMS를 지원하지 않으면 빈 목록.</summary>
        IReadOnlyList<MonitorSection> Sections { get; }
    }

    /// <summary>세 DBMS가 함께 갖는 섹션 id(이 순서대로).</summary>
    internal static class MonitorSections
    {
        /// <summary>DB 기본 정보(항목·값 두 열).</summary>
        public const string Info = "info";
        /// <summary>저장 공간: 테이블스페이스(Oracle·PostgreSQL)·데이터 파일(SQL Server)과 남은 용량.</summary>
        public const string Storage = "storage";
        /// <summary>로그: 리두 로그(Oracle)·WAL(PostgreSQL)·트랜잭션 로그(SQL Server).</summary>
        public const string Log = "log";
        /// <summary>잠금: 다른 세션을 막거나 기다리는 세션.</summary>
        public const string Locks = "locks";
        /// <summary>세션 목록.</summary>
        public const string Sessions = "sessions";

        public static readonly IReadOnlyList<string> Common = new[] { Info, Storage, Log, Locks, Sessions };
    }

    /// <summary>성능 모니터 섹션 하나: 제목·설명·필요 권한·조회 SQL.</summary>
    internal sealed class MonitorSection
    {
        public MonitorSection(string id, string title, string description, string privilege, string sql)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("id가 비었습니다.", nameof(id));
            if (string.IsNullOrWhiteSpace(title))
                throw new ArgumentException("title이 비었습니다.", nameof(title));
            if (string.IsNullOrWhiteSpace(sql))
                throw new ArgumentException("sql이 비었습니다.", nameof(sql));
            Id = id;
            Title = title;
            Description = description ?? "";
            Privilege = privilege ?? "";
            Sql = sql;
        }

        /// <summary>식별자(<see cref="MonitorSections"/> 또는 DBMS 고유). 소문자.</summary>
        public string Id { get; }

        /// <summary>탭 글.</summary>
        public string Title { get; }

        /// <summary>무엇을 어디서 읽는지 한 줄(표 위에 보임).</summary>
        public string Description { get; }

        /// <summary>필요한 권한(권한 오류 메시지에 보임). 예: "SELECT_CATALOG_ROLE".</summary>
        public string Privilege { get; }

        /// <summary>조회 SQL(바인드 없음). 결과 열 별칭이 화면 머리글.</summary>
        public string Sql { get; }
    }

    /// <summary>섹션이 없는 공급자(Generic)용.</summary>
    internal sealed class NoMonitor : IMonitorProvider
    {
        public static readonly NoMonitor Instance = new NoMonitor();

        public IReadOnlyList<MonitorSection> Sections { get { return Array.Empty<MonitorSection>(); } }
    }
}

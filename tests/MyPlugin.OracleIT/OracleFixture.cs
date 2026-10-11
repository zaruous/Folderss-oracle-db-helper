using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Oracle.ManagedDataAccess.Client;
using Xunit;
using Xunit.Abstractions;

namespace MyPlugin.OracleIT
{
    [CollectionDefinition(Name)]
    public sealed class OracleCollection : ICollectionFixture<OracleFixture>
    {
        public const string Name = "oracle";
    }

    /// <summary>ORACLE_IT_DSN이 없으면 건너뛴다(CI·Oracle 없는 PC).</summary>
    public sealed class OracleFactAttribute : FactAttribute
    {
        public OracleFactAttribute()
        {
            if (!OracleFixture.Enabled)
                Skip = OracleFixture.SkipReason;
        }
    }

    /// <summary>ORACLE_IT_DSN이 없으면 건너뛴다(CI·Oracle 없는 PC).</summary>
    public sealed class OracleTheoryAttribute : TheoryAttribute
    {
        public OracleTheoryAttribute()
        {
            if (!OracleFixture.Enabled)
                Skip = OracleFixture.SkipReason;
        }
    }

    /// <summary>
    /// SYSTEM 계정으로 시험 스키마(DBH_IT)와 객체를 만들고, 끝나면 지운다.
    /// 준비는 플러그인 코드가 아닌 ODP.NET 직접 호출로 한다 — 시험 대상과 섞이지 않게.
    /// 접속: ORACLE_IT_DSN(호스트:포트/서비스, 없으면 모든 시험을 건너뜀), ORACLE_IT_SYS_PW(SYSTEM 비밀번호, 기본 oracle).
    /// </summary>
    public sealed class OracleFixture : IDisposable
    {
        public const string SkipReason = "ORACLE_IT_DSN(예: localhost:1521/xe)이 없어 실제 Oracle 시험을 건너뜁니다.";

        public static bool Enabled
        {
            get { return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ORACLE_IT_DSN")); }
        }

        public const string User = "DBH_IT";
        public const string Password = "dbh_it_pw";
        // 연결 문자열 빌더의 따옴표 처리를 확인할 계정(비밀번호에 ; = ' 포함)
        public const string SpecialUser = "DBH_IT_SPECIAL";
        public const string SpecialPassword = "Pw;x=1'y";
        public const int OrderRows = 1000;

        public string Host { get; }
        public int Port { get; }
        public string Service { get; }
        public string ReportPath { get; }

        private readonly string _systemConnectionString;

        public OracleFixture()
        {
            // 시험을 모두 건너뛰어도 xUnit은 컬렉션 준비물을 만들 수 있다 — 서버에 붙지 않는다
            if (!Enabled)
                return;
            var dsn = Environment.GetEnvironmentVariable("ORACLE_IT_DSN").Trim();
            var hostPort = dsn.Split('/')[0];
            Host = hostPort.Split(':')[0];
            Port = int.Parse(hostPort.Split(':')[1]);
            Service = dsn.Split('/')[1];
            var sysPw = Environment.GetEnvironmentVariable("ORACLE_IT_SYS_PW") ?? "oracle";
            _systemConnectionString = new OracleConnectionStringBuilder
            {
                DataSource = dsn, UserID = "system", Password = sysPw, Pooling = false
            }.ConnectionString;
            ReportPath = Environment.GetEnvironmentVariable("ORACLE_IT_REPORT")
                ?? Path.Combine(AppContext.BaseDirectory, "oracle-it-report.txt");
            File.WriteAllText(ReportPath, "# Oracle 실측 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " · " + dsn + Environment.NewLine);

            // 플러그인은 Oracle 연결을 여는 모든 경로(CreateOracle·접속 테스트)에서 먼저 취소 방식을 정한다.
            // 연결을 하나라도 연 뒤에는 바꿀 수 없으므로(ORA-50099) 준비물의 SYSTEM 연결도 같은 순서를 따른다.
            DbSession.UseInBandBreak();
            Log("fixture", "OracleConfiguration.DisableOOB = " + OracleConfiguration.DisableOOB);

            var watch = Stopwatch.StartNew();
            Setup();
            Log("fixture", "시험 스키마 준비 " + watch.ElapsedMilliseconds + "ms");
        }

        public OracleConnectionProfile Profile(string name = "IT 개발", bool readOnly = false)
        {
            return new OracleConnectionProfile
            {
                Id = OracleConnectionStore.NewId(),
                Name = name,
                Host = Host,
                Port = Port,
                ServiceName = Service,
                UserId = User.ToLowerInvariant(),
                ReadOnly = readOnly,
                Color = "green"
            };
        }

        /// <summary>DbHelperView의 연결 흐름과 같은 경로로 세션을 연다.</summary>
        public async Task<DbSession> OpenSessionAsync()
        {
            var cs = OracleConnectionStore.BuildConnectionString(Profile(), Password, ShellLogic.ConnectTimeoutSeconds);
            var session = DbSession.CreateOracle(cs);
            await session.OpenAsync();
            return session;
        }

        public void Log(string area, string line)
        {
            lock (this)
                File.AppendAllText(ReportPath, "[" + area + "] " + line + Environment.NewLine);
        }

        private void Setup()
        {
            using (var c = new OracleConnection(_systemConnectionString))
            {
                c.Open();
                DropUsers(c);
                Exec(c, "CREATE USER " + User + " IDENTIFIED BY " + Password + " DEFAULT TABLESPACE USERS QUOTA UNLIMITED ON USERS");
                Exec(c, "GRANT CREATE SESSION, CREATE TABLE, CREATE VIEW, CREATE SEQUENCE, CREATE PROCEDURE TO " + User);
                Exec(c, "CREATE USER " + SpecialUser + " IDENTIFIED BY \"" + SpecialPassword + "\"");
                Exec(c, "GRANT CREATE SESSION TO " + SpecialUser);

                var s = User + ".";
                Exec(c, "CREATE TABLE " + s + "ORDERS ("
                      + " ORDER_ID NUMBER(10) CONSTRAINT PK_ORDERS PRIMARY KEY,"
                      + " CUSTOMER_NAME VARCHAR2(50 CHAR) NOT NULL,"
                      + " AMOUNT NUMBER(12,2),"
                      + " NOTE NVARCHAR2(100),"
                      + " CREATED_AT DATE,"
                      + " MEMO CLOB)");
                Exec(c, "INSERT INTO " + s + "ORDERS"
                      + " SELECT LEVEL, '고객' || LEVEL, ROUND(LEVEL * 1.5, 2),"
                      + " CASE WHEN MOD(LEVEL, 10) = 0 THEN NULL ELSE N'메모' || LEVEL END,"
                      + " DATE '2026-01-01' + LEVEL + 1/24,"
                      + " CASE WHEN LEVEL = 1 THEN TO_CLOB(RPAD('x', 4000, 'x')) || TO_CLOB(RPAD('y', 2000, 'y')) END"
                      + " FROM DUAL CONNECT BY LEVEL <= " + OrderRows);
                Exec(c, "CREATE TABLE " + s + "ORDER_ITEMS ("
                      + " ITEM_ID NUMBER(10) PRIMARY KEY,"
                      + " ORDER_ID NUMBER(10) NOT NULL REFERENCES " + s + "ORDERS(ORDER_ID),"
                      + " QTY NUMBER(5))");
                Exec(c, "INSERT INTO " + s + "ORDER_ITEMS SELECT LEVEL, LEVEL, MOD(LEVEL, 7) + 1 FROM DUAL CONNECT BY LEVEL <= 30");
                Exec(c, "COMMIT");
                Exec(c, "CREATE VIEW " + s + "V_ORDER_SUMMARY AS SELECT ORDER_ID, AMOUNT FROM " + s + "ORDERS");
                Exec(c, "CREATE SEQUENCE " + s + "SEQ_ORDER");
                Exec(c, "CREATE PROCEDURE " + s + "P_TOUCH(p_id NUMBER) AS BEGIN UPDATE " + s + "ORDERS SET AMOUNT = AMOUNT WHERE ORDER_ID = p_id; END;");
                Exec(c, "CREATE FUNCTION " + s + "F_DOUBLE(x NUMBER) RETURN NUMBER AS BEGIN RETURN x * 2; END;");
                Exec(c, "CREATE PACKAGE " + s + "PKG_UTIL AS FUNCTION ver RETURN VARCHAR2; END;");
                Exec(c, "CREATE PACKAGE BODY " + s + "PKG_UTIL AS FUNCTION ver RETURN VARCHAR2 IS BEGIN RETURN '1.0'; END; END;");
                // 테이블 정의서(F4): 주석·기본값·검사 조건·보조 인덱스
                Exec(c, "COMMENT ON TABLE " + s + "ORDERS IS '주문'");
                Exec(c, "COMMENT ON COLUMN " + s + "ORDERS.ORDER_ID IS '주문 번호'");
                Exec(c, "COMMENT ON COLUMN " + s + "ORDERS.CUSTOMER_NAME IS '고객 이름'");
                Exec(c, "ALTER TABLE " + s + "ORDERS MODIFY CREATED_AT DEFAULT SYSDATE");
                Exec(c, "ALTER TABLE " + s + "ORDER_ITEMS ADD CONSTRAINT CK_ORDER_ITEMS_QTY CHECK (QTY > 0)");
                Exec(c, "CREATE INDEX " + s + "IX_ORDERS_NAME ON " + s + "ORDERS(CUSTOMER_NAME, CREATED_AT)");
                Exec(c, "BEGIN DBMS_STATS.GATHER_TABLE_STATS('" + User + "', 'ORDERS'); END;");

                // GCM 정의서(F5): 공통 코드 표 — 열 이름은 일부러 별칭 계약과 다르게(사용자 SQL의 AS로 맞춘다)
                Exec(c, "CREATE TABLE " + s + "GCM_CODE (CODE_GROUP VARCHAR2(30) NOT NULL, GROUP_NM VARCHAR2(100), CODE VARCHAR2(30) NOT NULL, CODE_NM VARCHAR2(100), SORT_SEQ NUMBER, USE_FLAG CHAR(1), PLANT_CD VARCHAR2(10))");
                Exec(c, "INSERT ALL"
                    + " INTO " + s + "GCM_CODE VALUES ('ORDER_STATUS', '주문 상태', 'B', '출고', 2, 'Y', 'P1')"
                    + " INTO " + s + "GCM_CODE VALUES ('ORDER_STATUS', '주문 상태', 'A', '접수', 1, 'Y', 'P1')"
                    + " INTO " + s + "GCM_CODE VALUES ('ORDER_STATUS', '주문 상태', 'Z', '취소', 10, 'N', NULL)"
                    + " INTO " + s + "GCM_CODE VALUES ('YN', '예/아니오', 'Y', '예', 1, 'Y', NULL)"
                    + " INTO " + s + "GCM_CODE VALUES ('YN', '예/아니오', 'N', '아니오', 2, 'Y', NULL)"
                    + " SELECT 1 FROM DUAL");
                Exec(c, "COMMIT");
            }
        }

        public void Dispose()
        {
            if (!Enabled)
                return;
            try
            {
                using (var c = new OracleConnection(_systemConnectionString))
                {
                    c.Open();
                    DropUsers(c);
                }
                Log("fixture", "시험 스키마 정리 완료");
            }
            catch (Exception ex)
            {
                Log("fixture", "정리 실패: " + ex.Message);
            }
        }

        /// <summary>시험 계정의 실행 중(ACTIVE) 세션을 서버에서 끊는다 — 취소가 듣지 않을 때 정리용.</summary>
        public void KillActiveSessions()
        {
            using (var c = new OracleConnection(_systemConnectionString))
            {
                c.Open();
                Exec(c, "BEGIN FOR s IN (SELECT SID, SERIAL# SN FROM V$SESSION WHERE USERNAME = '" + User + "' AND STATUS = 'ACTIVE') LOOP"
                      + " BEGIN EXECUTE IMMEDIATE 'ALTER SYSTEM KILL SESSION ''' || s.SID || ',' || s.SN || ''' IMMEDIATE';"
                      // ORA-00031: 바로 못 끊어 '끊기 예정'으로 표시됨 — 정리로는 충분하다
                      + " EXCEPTION WHEN OTHERS THEN IF SQLCODE != -31 THEN RAISE; END IF; END; END LOOP; END;");
            }
        }

        private static void DropUsers(OracleConnection c)
        {
            foreach (var u in new[] { User, SpecialUser })
            {
                // 이전 실행의 세션이 남아 있으면 DROP USER가 ORA-01940으로 실패한다 — 세션을 먼저 끊는다
                Exec(c, "BEGIN FOR s IN (SELECT SID, SERIAL# SN FROM V$SESSION WHERE USERNAME = '" + u + "') LOOP"
                      + " EXECUTE IMMEDIATE 'ALTER SYSTEM KILL SESSION ''' || s.SID || ',' || s.SN || ''' IMMEDIATE'; END LOOP;"
                      + " EXECUTE IMMEDIATE 'DROP USER " + u + " CASCADE';"
                      + " EXCEPTION WHEN OTHERS THEN IF SQLCODE != -1918 THEN RAISE; END IF; END;");
            }
        }

        private static void Exec(OracleConnection c, string sql)
        {
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }
    }

    /// <summary>시험 출력과 보고 파일에 함께 남긴다.</summary>
    public abstract class OracleTestBase
    {
        protected readonly OracleFixture Db;
        private readonly ITestOutputHelper _output;
        private readonly string _area;

        protected OracleTestBase(OracleFixture db, ITestOutputHelper output, string area)
        {
            Db = db;
            _output = output;
            _area = area;
        }

        protected void Log(string line)
        {
            _output.WriteLine(line);
            Db.Log(_area, line);
        }

        protected static SqlStatement Sql(string text)
        {
            return SqlScript.Parse(text);
        }

        protected static SqlQuery Query(string sql, params (string Name, object Value)[] binds)
        {
            var q = new SqlQuery { Sql = sql };
            foreach (var b in binds)
                q.Parameters.Add(new KeyValuePair<string, object>(b.Name, b.Value));
            return q;
        }

        protected static async Task<T> Timed<T>(Func<Task<T>> work, Action<long> elapsedMs)
        {
            var w = Stopwatch.StartNew();
            var r = await work();
            elapsedMs(w.ElapsedMilliseconds);
            return r;
        }
    }
}

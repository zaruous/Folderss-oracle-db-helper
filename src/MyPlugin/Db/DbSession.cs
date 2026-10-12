using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Oracle.ManagedDataAccess.Client;

namespace MyPlugin
{
    /// <summary>
    /// 조회 결과의 열린 커서(DbDataReader). 다음 행은 <see cref="DbSession.FetchAsync"/>로 이어서 읽는다(다시 조회하지 않음).
    /// "더 있음"은 한 행을 미리 읽어 판단하므로 미리 읽은 행은 다음 Fetch에서 먼저 돌려준다(행을 잃지 않음).
    /// </summary>
    public sealed class QueryCursor
    {
        public List<ResultColumn> Columns { get; } = new List<ResultColumn>();

        /// <summary>지금까지 돌려준 행 수.</summary>
        public int Fetched { get; internal set; }

        /// <summary>더 읽을 행이 있으면 true(커서 열림). 마지막 행까지 읽으면 false가 되고 커서는 닫힌다.</summary>
        public bool HasMore { get; internal set; }

        /// <summary>닫혔으면 true(끝까지 읽음, 새 실행, 세션 닫힘, CloseCursorAsync).</summary>
        public bool IsClosed { get; internal set; }

        /// <summary>이 커서를 연 세션.</summary>
        internal DbSession Owner { get; set; }

        internal DbCommand Command { get; set; }

        internal DbDataReader Reader { get; set; }

        /// <summary>"더 있음"을 판단하려고 미리 읽은 행. 다음 Fetch에서 먼저 돌려준다.</summary>
        internal string[] Lookahead { get; set; }

        /// <summary>열마다 값을 읽는 방법(Oracle). null이면 모두 GetValue.</summary>
        internal ColumnRead[] ReadModes { get; set; }

        /// <summary>SELECT … FOR UPDATE 커서. 트랜잭션이 끝나면 더 읽을 수 없다(ORA-01002).</summary>
        internal bool ForUpdate { get; set; }
    }

    public sealed class ExecuteResult
    {
        public SqlKind Kind { get; set; }

        /// <summary>Query만. 다음 행을 가져올 때 쓴다(HasMore가 false면 이미 닫힘).</summary>
        public QueryCursor Cursor { get; set; }

        /// <summary>Query의 첫 묶음. 값은 ValueFormatter.Format 결과(null = NULL).</summary>
        public List<string[]> Rows { get; set; }

        /// <summary>DML 영향 행 수. 모르면 -1.</summary>
        public int RecordsAffected { get; set; } = -1;

        public TimeSpan Elapsed { get; set; }

        /// <summary>COMMIT·ROLLBACK·DDL(자동 커밋)로, 또는 PL/SQL 블록 안의 커밋·롤백으로 트랜잭션이 끝났으면(열린 트랜잭션이 없으면) true.</summary>
        public bool TransactionEnded { get; set; }

        /// <summary>메시지 탭에 쓸 한 줄. 예: "200행 · 더 있음", "3행 변경됨 (커밋 전)", "커밋함", "실행함(DDL은 자동 커밋됨)".</summary>
        public string Summary { get; set; }

        /// <summary>
        /// 실행은 됐지만 알려야 할 문제(오류로 보인다). 예: 컴파일 오류가 있는 PL/SQL 객체(ORA-24344 — 예외가 아니라 경고로만 온다). 없으면 null.
        /// </summary>
        public string Warning { get; set; }

        /// <summary>Warning의 자세한 줄들(ALL_ERRORS의 "줄 3, 열 5: PLS-00103: …"). 없으면 빈 목록.</summary>
        public List<string> WarningDetails { get; } = new List<string>();
    }

    /// <summary>사용자 SQL을 통째로 읽은 결과(<see cref="DbSession.QueryTableAsync"/>): 열 이름(결과 순서)과 행(글자, NULL은 null).</summary>
    public sealed class QueryTable
    {
        public List<string> Columns { get; } = new List<string>();
        public List<string[]> Rows { get; } = new List<string[]>();
    }

    /// <summary>열 값을 읽는 방법.</summary>
    internal enum ColumnRead
    {
        /// <summary>DbDataReader.GetValue(.NET 형식)</summary>
        Value,
        /// <summary>GetProviderSpecificValue(OracleDecimal 등 — 큰 NUMBER·TIMESTAMP WITH TIME ZONE 보존)</summary>
        ProviderSpecific,
        /// <summary>GetString(객체·컬렉션 형식은 JSON, REF는 16진수 — 형식 매핑이 없으면 다른 방법은 예외)</summary>
        Text
    }

    /// <summary>같은 세션에서 다른 실행이 진행 중일 때(DB마다 세션 1개, 한 번에 한 문장).</summary>
    public sealed class SessionBusyException : InvalidOperationException
    {
        public SessionBusyException() : base("같은 DB에서 다른 실행이 진행 중입니다. 끝난 뒤 다시 실행하세요.") { }
    }

    /// <summary>커밋·롤백이 실패했고 서버에서 트랜잭션이 끝났을 때(ORA-02091 롤백 등). 커밋 대기 변경은 남아 있지 않다. InnerException이 원래 오류.</summary>
    public sealed class TransactionEndedException : Exception
    {
        public TransactionEndedException(Exception inner)
            : base(DbSession.DescribeError(inner) + " — 서버에서 트랜잭션이 끝나 커밋 대기 변경이 남아 있지 않습니다.", inner)
        {
        }
    }

    /// <summary>
    /// DB(접속) 하나의 세션. 연결 1개를 오래 유지하고 모든 연결 사용을 내부 잠금(SemaphoreSlim) 아래에서 한다.
    /// - ExecuteAsync·FetchAsync·CommitAsync·RollbackAsync·CloseCursorAsync: 잠금을 바로 못 얻으면 <see cref="SessionBusyException"/>(기다리지 않음).
    /// - RunAsync(트리·검색 같은 메타데이터 조회): 잠금을 기다린다(ct로 기다림 취소).
    /// - 실제 DB 호출은 Task.Run 안에서 동기 ADO.NET으로 한다(공급자의 async가 동기로 동작해도 UI가 멈추지 않게).
    /// - 자동 커밋 끔: DML·PL/SQL·SELECT FOR UPDATE·SAVEPOINT 전에 트랜잭션을 시작하고(없으면), 그 뒤 모든 명령의 Transaction에 넣는다.
    /// - DDL: 트랜잭션이 있으면 먼저 Commit(Oracle의 묵시적 커밋과 같은 결과를 모든 공급자에서 보장) 후 트랜잭션 없이 실행. TransactionEnded = true.
    ///   컴파일 오류가 있는 PL/SQL 객체(ORA-24344)는 예외가 아니라 InfoMessage 경고로만 오므로 ExecuteResult.Warning으로 알린다.
    /// - PL/SQL 블록·CALL 뒤에는 서버에 트랜잭션이 남았는지 물어(Oracle) 블록 안의 커밋·롤백을 대기 표시에 반영한다.
    /// - LOCK TABLE은 트랜잭션 안에서 실행하고 커밋 대기(잠금 보유)로 표시한다.
    /// - OracleConnection은 연 뒤 AutoCommit = false: 트랜잭션 객체가 없는 동안에도 ODP.NET이 명령마다 커밋하지 않게.
    /// - 커밋·롤백이 실패하면 ODP.NET이 끝내 버린 트랜잭션 객체를 버리고, 서버에 트랜잭션이 남았으면 새 객체로 이어 받는다.
    /// - COMMIT·ROLLBACK 문장은 SQL로 보내지 않고 CommitAsync·RollbackAsync와 같게 처리한다(트랜잭션 객체가 없어도 서버 트랜잭션까지 끝낸다).
    /// - Query: fetchCount+1행을 읽어 HasMore 판단. 끝까지 읽었으면 리더를 닫는다. 같은 연결의 다른 열린 커서는 그대로 둔다.
    /// - 값: OracleDataReader면 GetProviderSpecificValue(큰 NUMBER·TIMESTAMP WITH TIME ZONE 보존), 아니면 GetValue → ValueFormatter.Format.
    ///   IDisposable 값(OracleClob·OracleBlob 등)은 형식화 직후 Dispose한다.
    /// - OracleCommand면 BindByName = true, InitialLOBFetchSize = ValueFormatter.MaxTextLength. CommandTimeout = 0(사용자가 취소).
    /// - Cancel: 실행 중인 명령의 DbCommand.Cancel()(다른 스레드에서 호출 가능). 취소된 실행은 예외로 끝나고 세션은 계속 쓸 수 있다.
    /// - 연결이 끊긴 오류(ORA-03113, 03114, 03135, 12570, 12571, 02396, 01012 등)가 나면 IsBroken = true.
    /// 테스트는 SQLite(Microsoft.Data.Sqlite) 연결로 한다 — Oracle 전용 코드는 형식 검사(is OracleCommand)로 분기한다.
    /// </summary>
    public sealed class DbSession : IDisposable
    {
        private const int CancelErrorNumber = 1013;
        private const string CancelErrorCode = "ORA-01013";
        // "success with compilation error": 컴파일 오류가 있는 채로 PL/SQL 객체·뷰를 만들었다
        private const int CompileErrorWarning = 24344;
        // NULL이면 서버에 열린 트랜잭션이 없다(만들지 않고 묻기만 함)
        private const string OracleTransactionProbe = "SELECT DBMS_TRANSACTION.LOCAL_TRANSACTION_ID FROM DUAL";
        private const string UnreadableValue = "(OBJECT)";

        // 연결이 끊겼다고 볼 Oracle 오류 번호(세션 종료·로그오프: 603 치명 오류, 1092 인스턴스 종료, 2392·2396·2399 자원 한도)
        private static readonly HashSet<int> BrokenErrorNumbers = new HashSet<int> { 28, 603, 1012, 1092, 2392, 2396, 2399, 3113, 3114, 3135, 12537, 12547, 12570, 12571 };

        // 연결이 닫힌 상태에서 공급자가 던지는 InvalidOperationException 문구(ODP.NET, SQLite, 일반 ADO.NET)
        private static readonly string[] ClosedConnectionPhrases =
        {
            "connection must be open", "can only be called when the connection is open", "connection is closed", "connection was closed"
        };

        // Dispose가 실행 중인 작업을 기다리는 최대 시간
        private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(2);

        // UseInBandBreak을 이미 시도했음(1). 성공 여부와 관계없이 한 번만 시도한다.
        private static int _breakConfigured;

        private readonly DbConnection _connection;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private readonly object _executingSync = new object();
        private readonly List<QueryCursor> _cursors = new List<QueryCursor>();
        // 지금 실행하는 문장이 받은 경고 번호(OracleConnection.InfoMessage). 실행 스레드에서만 쓰지만 이벤트라 잠근다.
        private readonly List<int> _warnings = new List<int>();

        private DbCommand _executing;
        // 지금 작업의 취소 토큰. 잠금을 쥔 작업 스레드(RunDb 안)에서만 읽고 쓴다.
        private CancellationToken _operationToken;
        private DbTransaction _transaction;
        private volatile bool _opened;
        private volatile bool _closed;
        private volatile bool _broken;
        private volatile bool _hasPending;
        private volatile bool _countUnknown;
        private volatile bool _locksHeld;
        // 이 트랜잭션에서 LOCK TABLE을 실행했다. 그 잠금(TM)은 트랜잭션 ID 없이 잡혀 서버 확인(LOCAL_TRANSACTION_ID)이 NULL이어도 남아 있을 수 있다.
        private volatile bool _tableLocked;
        private volatile int _pendingRows;
        private int _disposed;

        /// <summary>연결을 받아 세션을 만든다(아직 안 열림). 세션이 연결의 수명을 맡는다. 공급자는 연결 형식으로 고른다(시험의 SQLite는 Generic).</summary>
        public DbSession(DbConnection connection) : this(connection, null)
        {
        }

        /// <summary>연결과 공급자로 세션을 만든다. provider가 null이면 연결 형식으로 고른다.</summary>
        internal DbSession(DbConnection connection, IDbProvider provider)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            _connection = connection;
            Provider = provider ?? DbProviders.For(connection);
            var oracle = connection as OracleConnection;
            if (oracle != null)
            {
                TransactionProbeSql = OracleTransactionProbe;
                // ODP.NET은 처리기가 있을 때만 경고(ORA-24344 등)를 알린다
                oracle.InfoMessage += Connection_InfoMessage;
            }
        }

        /// <summary>이 세션의 DBMS 공급자(방언·데이터 사전·오류 판정). 트리·정의서 창이 세션에서 꺼내 쓴다.</summary>
        internal IDbProvider Provider { get; }

        /// <summary>이 연결의 SQL 방언(공급자의 것). 사용자 SQL을 감싸는 쪽(CodeSpecQueries 등)이 쓴다.</summary>
        internal ISqlDialect Dialect { get { return Provider.Dialect; } }

        /// <summary>
        /// 서버에 열린 트랜잭션이 있는지 묻는 문장. 값이 NULL이면 없음. null이면 묻지 않는다(알 수 없음 — 대기로 본다).
        /// Oracle은 DBMS_TRANSACTION.LOCAL_TRANSACTION_ID, 그 밖의 공급자는 null(시험에서 바꾼다).
        /// </summary>
        internal string TransactionProbeSql { get; set; }

        /// <summary>OracleConnection으로 세션을 만든다(= <see cref="Create"/>(Oracle 공급자)).</summary>
        public static DbSession CreateOracle(string connectionString)
        {
            return Create(DbProviders.Oracle, connectionString);
        }

        /// <summary>공급자의 연결로 세션을 만든다(아직 안 열림). 공급자의 프로세스 설정(Oracle in-band break)을 먼저 한다.</summary>
        internal static DbSession Create(IDbProvider provider, string connectionString)
        {
            if (provider == null)
                throw new ArgumentNullException(nameof(provider));
            provider.ConfigureProcess();
            return new DbSession(provider.CreateConnection(connectionString), provider);
        }

        /// <summary>
        /// 취소(DbCommand.Cancel)를 in-band break로 보낸다. 기본값 OOB(TCP 긴급 데이터)는 Docker Desktop의 포트 포워딩이나
        /// 일부 방화벽·NAT·VPN이 전달하지 않아, 서버가 취소를 받지 못하고 문장이 끝날 때까지 세션이 바쁜 채로 남는다
        /// (실측: Docker의 Oracle 12.1에서 OOB 취소는 10분이 지나도 멈추지 않았고, in-band 취소는 10~20ms에 ORA-01013으로 끝남).
        /// OracleConfiguration은 이 플러그인이 불러온 ODP.NET에만 적용된다(Folderss는 플러그인마다 따로 로드한다).
        /// 이 값은 프로세스에서 연결을 하나라도 연 뒤에는 바꿀 수 없으므로(ORA-50099) Oracle 연결을 여는 모든 경로(CreateOracle·접속 테스트)가 먼저 부른다.
        /// 늦게 불려 바꾸지 못해도 연결은 막지 않는다(취소만 OOB로 남음).
        /// </summary>
        internal static void UseInBandBreak()
        {
            if (Interlocked.Exchange(ref _breakConfigured, 1) != 0)
                return;
            try
            {
                OracleConfiguration.DisableOOB = true;
            }
            catch (InvalidOperationException)
            {
                // ORA-50099: 이미 연 연결이 있어 바꿀 수 없다
            }
        }

        public bool IsOpen { get { return _opened && !_closed && ConnectionIsOpen(); } }

        /// <summary>연결이 끊긴 오류가 났음. 다시 연결해야 한다.</summary>
        public bool IsBroken { get { return _broken; } }

        /// <summary>실행·가져오기·커밋 등이 진행 중(잠금을 잡고 있음).</summary>
        public bool IsBusy { get { return _lock.CurrentCount == 0; } }

        /// <summary>커밋하지 않은 변경(또는 FOR UPDATE·LOCK TABLE 잠금)이 있음.</summary>
        public bool HasPendingChanges { get { return _hasPending; } }

        /// <summary>커밋 대기 DML 영향 행 수의 합(표시용).</summary>
        public int PendingRowCount { get { return _pendingRows; } }

        /// <summary>PL/SQL 실행 등으로 대기 행 수를 알 수 없음.</summary>
        public bool PendingCountUnknown { get { return _countUnknown; } }

        /// <summary>
        /// 표시용. 대기가 없으면 null, "커밋 대기 3행", 수를 모르면 "커밋 대기(행 수 모름)".
        /// SELECT … FOR UPDATE·LOCK TABLE 잠금만 있으면 "커밋 대기(잠금 보유)", 변경도 있으면 뒤에 " · 잠금".
        /// </summary>
        public string PendingText
        {
            get
            {
                if (!_hasPending)
                    return null;
                var locks = _locksHeld ? " · 잠금" : "";
                if (_countUnknown)
                    return "커밋 대기(행 수 모름)" + locks;
                if (_locksHeld && _pendingRows == 0)
                    return "커밋 대기(잠금 보유)";
                return "커밋 대기 " + _pendingRows.ToString(CultureInfo.InvariantCulture) + "행" + locks;
            }
        }

        public async Task OpenAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfClosed();
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfClosed();
                await Task.Run(() =>
                {
                    if (_connection.State != ConnectionState.Open)
                        _connection.Open();
                    // 트랜잭션 객체가 없는 동안(커밋·롤백이 실패한 뒤 등) ODP.NET이 DML을 문장마다 커밋하지 않게 한다.
                    // 기본값(true)이면 OracleTransaction이 끝날 때마다 자동 커밋을 다시 켠다.
                    var oracle = _connection as OracleConnection;
                    if (oracle != null && oracle.AutoCommit)
                        oracle.AutoCommit = false;
                }, cancellationToken).ConfigureAwait(false);
                _opened = true;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>문장 하나를 실행한다. 이 세션에서 다른 작업 중이면 SessionBusyException.</summary>
        public async Task<ExecuteResult> ExecuteAsync(SqlStatement statement, int fetchCount, CancellationToken cancellationToken = default)
        {
            if (statement == null)
                throw new ArgumentNullException(nameof(statement));
            if (fetchCount < 0)
                throw new ArgumentOutOfRangeException(nameof(fetchCount));
            if (!EndsTransaction(statement) && string.IsNullOrWhiteSpace(statement.Text))
                throw new ArgumentException("실행할 문장이 없습니다.", nameof(statement));
            cancellationToken.ThrowIfCancellationRequested();
            EnterNow();
            try
            {
                return await RunDb(() => Execute(statement, fetchCount), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// 바인드 변수가 있는 DML 하나(결과 편집의 UPDATE·DELETE)를 실행하고 영향 행 수를 돌려준다. 편집기의 DML처럼 트랜잭션 안에서 실행해
        /// 커밋 대기로 센다([커밋]·[롤백]으로 끝냄). 바인드 이름은 접두(':'·'@') 없이(SqlQuery.Parameters). 이 세션에서 다른 작업 중이면 SessionBusyException.
        /// </summary>
        public async Task<int> ExecuteDmlAsync(SqlQuery query, CancellationToken cancellationToken = default)
        {
            if (query == null)
                throw new ArgumentNullException(nameof(query));
            if (string.IsNullOrWhiteSpace(query.Sql))
                throw new ArgumentException("실행할 문장이 없습니다.", nameof(query));
            cancellationToken.ThrowIfCancellationRequested();
            EnterNow();
            try
            {
                return await RunDb(() => ExecuteDmlCore(query), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }

        // 잠금을 쥔 작업 스레드에서만 부른다
        private int ExecuteDmlCore(SqlQuery query)
        {
            ClearWarnings();
            EnsureTransaction();
            int affected;
            using (var command = CreateCommand(query.Sql))
            {
                foreach (var pair in query.Parameters)
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = pair.Key;
                    parameter.Value = pair.Value ?? DBNull.Value;
                    command.Parameters.Add(parameter);
                }
                SetExecuting(command);
                try
                {
                    affected = command.ExecuteNonQuery();
                }
                finally
                {
                    SetExecuting(null);
                }
            }
            _hasPending = true;
            if (affected >= 0)
                _pendingRows = (int)Math.Min(int.MaxValue, (long)_pendingRows + affected);
            else
                _countUnknown = true;
            return affected;
        }

        /// <summary>열린 커서에서 다음 count행. 끝에 닿으면 cursor.HasMore = false, 커서를 닫는다. 닫힌 커서면 빈 목록.</summary>
        public async Task<List<string[]>> FetchAsync(QueryCursor cursor, int count, CancellationToken cancellationToken = default)
        {
            if (cursor == null)
                throw new ArgumentNullException(nameof(cursor));
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (cursor.IsClosed)
                return new List<string[]>();
            ThrowIfForeign(cursor);
            cancellationToken.ThrowIfCancellationRequested();
            EnterNow();
            try
            {
                return await RunDb(() => FetchCore(cursor, count), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>커서를 닫는다(이미 닫혔으면 아무것도 안 함).</summary>
        public async Task CloseCursorAsync(QueryCursor cursor)
        {
            if (cursor == null)
                throw new ArgumentNullException(nameof(cursor));
            if (cursor.IsClosed)
                return;
            ThrowIfForeign(cursor);
            EnterNow();
            try
            {
                await RunDb(() =>
                {
                    CloseCursorCore(cursor);
                    return true;
                }, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// 사용자 SQL을 통째로 읽는다(GCM 정의서 등): 열 이름과 모든 행(값은 결과 격자와 같은 글자 — NULL은 null, LOB·큰 NUMBER도 글자).
        /// maxRows가 있으면 그만큼만 읽는다(0이면 열 이름만). 잠금을 기다리고(RunAsync), cancellationToken으로 실행 중인 명령을 취소한다.
        /// 바인드 변수는 받지 않는다 — 사용자 SQL은 값을 직접 쓴다.
        /// </summary>
        public Task<QueryTable> QueryTableAsync(string sql, int? maxRows, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sql))
                throw new ArgumentException("SQL이 비어 있습니다.", nameof(sql));
            return RunAsync((connection, transaction) =>
            {
                using (var command = CreateCommand(sql))
                using (cancellationToken.Register(() => CancelQuietly(command)))
                using (var reader = command.ExecuteReader())
                {
                    var table = new QueryTable();
                    var count = reader.FieldCount;
                    var oracle = reader is OracleDataReader;
                    var modes = new ColumnRead[count];
                    for (var i = 0; i < count; i++)
                    {
                        table.Columns.Add(reader.GetName(i));
                        modes[i] = oracle ? OracleReadMode(reader.GetDataTypeName(i)) : ColumnRead.Value;
                    }
                    while ((!maxRows.HasValue || table.Rows.Count < maxRows.Value) && reader.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        table.Rows.Add(ReadRow(reader, modes));
                    }
                    return table;
                }
            }, cancellationToken);
        }

        /// <summary>메타데이터 조회 등: 잠금을 기다린 뒤 work(연결, 현재 트랜잭션 또는 null)를 Task.Run에서 실행한다.</summary>
        public async Task<T> RunAsync<T>(Func<DbConnection, DbTransaction, T> work, CancellationToken cancellationToken = default)
        {
            if (work == null)
                throw new ArgumentNullException(nameof(work));
            ThrowIfNotReady();
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfNotReady();
                return await RunDb(() => work(_connection, _transaction), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// 메타데이터 조회(트리·검색): 잠금을 기다린 뒤 query를 실행하고 행마다 read로 읽는다.
        /// 명령 설정(Oracle BindByName 등)·트랜잭션은 사용자 실행과 같다. 바인드 이름은 ':' 없이(SqlQuery.Parameters).
        /// cancellationToken은 잠금 기다림과 실행 중인 명령(DbCommand.Cancel)을 모두 취소한다.
        /// </summary>
        public Task<List<T>> QueryAsync<T>(SqlQuery query, Func<IDataRecord, T> read, CancellationToken cancellationToken = default)
        {
            if (query == null)
                throw new ArgumentNullException(nameof(query));
            if (read == null)
                throw new ArgumentNullException(nameof(read));
            return RunAsync((connection, transaction) => QueryRows(query, read, cancellationToken), cancellationToken);
        }

        // 잠금을 쥔 작업 스레드에서만 부른다
        private List<T> QueryRows<T>(SqlQuery query, Func<IDataRecord, T> read, CancellationToken cancellationToken)
        {
            using (var command = CreateCommand(query.Sql))
            {
                foreach (var pair in query.Parameters)
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = pair.Key;
                    parameter.Value = pair.Value ?? DBNull.Value;
                    command.Parameters.Add(parameter);
                }
                using (cancellationToken.Register(() => CancelQuietly(command)))
                using (var reader = command.ExecuteReader())
                {
                    var rows = new List<T>();
                    while (reader.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        rows.Add(read(reader));
                    }
                    return rows;
                }
            }
        }

        private static void CancelQuietly(DbCommand command)
        {
            try
            {
                command.Cancel();
            }
            catch (Exception)
            {
                // 이미 끝났거나 정리된 명령 — 취소할 것이 없다
            }
        }

        /// <summary>실행 중인 명령을 취소한다. 실행 중이 아니면 아무것도 안 함.</summary>
        public void Cancel()
        {
            // 잠금 안에서 취소해야 실행 스레드가 명령을 Dispose한 뒤에 Cancel하는 일이 없다.
            // ODP.NET의 Cancel은 중단 신호만 보내고 실행 스레드를 기다리지 않으므로 교착은 없다.
            lock (_executingSync)
            {
                var command = _executing;
                if (command == null)
                    return;
                try
                {
                    command.Cancel();
                }
                catch (Exception)
                {
                    // 막 끝났거나 연결이 끊긴 명령의 Cancel이 던지는 예외는 취소 실패일 뿐이다
                }
            }
        }

        public async Task CommitAsync()
        {
            EnterNow();
            try
            {
                await RunDb(() =>
                {
                    CommitCore();
                    return true;
                }, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task RollbackAsync()
        {
            EnterNow();
            try
            {
                await RunDb(() =>
                {
                    RollbackCore();
                    return true;
                }, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// 닫기: 실행 중이면 취소하고 끝나기를 기다린 뒤, commit이면 커밋·아니면 롤백하고 모든 커서와 연결을 닫는다.
        /// 커밋·롤백이 실패해도 세션은 끝까지 닫은 뒤 그 예외를 낸다. 단 끊긴 연결의 롤백 실패는 무시한다(서버가 롤백한다).
        /// 끊긴 연결의 커밋 실패는 무시하지 않는다 — 변경이 저장되지 않았음을 호출자가 알아야 한다.
        /// </summary>
        public async Task CloseAsync(bool commit)
        {
            if (_closed)
                return;
            Cancel();
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_closed)
                    return;
                await Task.Run(() => CloseCore(commit)).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>즉시 정리(앱 종료 등): 취소 → 커서 닫기 → 롤백 → 연결 닫기. 예외를 밖으로 내지 않는다.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _closed = true;
            Cancel();
            var locked = false;
            try
            {
                // 취소가 듣지 않는 작업이 잠금을 쥐고 있어도 종료는 막지 않는다
                locked = _lock.Wait(DisposeWait);
                CloseCursors();
                var transaction = _transaction;
                _transaction = null;
                if (transaction != null)
                {
                    try
                    {
                        transaction.Rollback();
                    }
                    catch (Exception)
                    {
                        // 연결을 닫으면 서버가 어차피 롤백한다
                    }
                    DisposeQuietly(transaction);
                }
                CloseConnection();
            }
            catch (Exception)
            {
                // Dispose는 예외를 내지 않는다
            }
            finally
            {
                if (locked)
                    _lock.Release();
            }
        }

        /// <summary>
        /// 사람이 읽을 오류 문장. AggregateException·TargetInvocationException을 벗긴다.
        /// ORA-01013(취소) → "실행을 취소했습니다.", 연결 끊김 → "DB 연결이 끊겼습니다. 다시 연결하세요. (ORA-…)", 그 밖은 예외 메시지(ORA-번호 포함) 첫 줄.
        /// 안쪽 예외에 다른 오류 번호가 있으면(연결 실패 ORA-50201 ← ORA-12514 등) 그 원인을 앞에 둔다: "ORA-12514: … (ORA-50201)".
        /// </summary>
        public static string DescribeError(Exception ex)
        {
            ex = Unwrap(ex);
            if (ex == null)
                return "알 수 없는 오류가 발생했습니다.";
            if (IsCancellation(ex))
                return "실행을 취소했습니다.";
            if (IsBrokenError(ex))
            {
                var code = BrokenErrorNumber(ex);
                return "DB 연결이 끊겼습니다. 다시 연결하세요." + (code.HasValue ? " (" + OracleCode(code.Value) + ")" : "");
            }
            return WithInnerCause(ex, FirstLine(ex));
        }

        /// <summary>취소로 끝난 실행인지(ORA-01013, OperationCanceledException 등).</summary>
        public static bool IsCancellation(Exception ex)
        {
            for (var e = Unwrap(ex); e != null; e = e.InnerException)
            {
                if (e is OperationCanceledException)
                    return true;
                if (e is OracleException oracle && oracle.Number == CancelErrorNumber)
                    return true;
                if (DbProviders.All.Any(p => p.IsCancellation(e)))
                    return true;
                // 다른 예외로 감싸여 번호 없이 메시지에만 남은 경우
                if (e.Message != null && e.Message.IndexOf(CancelErrorCode, StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }

        /// <summary>연결이 끊겨 다시 연결해야 하는 오류인지(ORA-03113 등, 닫힌 연결에서의 InvalidOperationException).</summary>
        internal static bool IsBrokenError(Exception ex)
        {
            ex = Unwrap(ex);
            if (BrokenErrorNumber(ex).HasValue)
                return true;
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is InvalidOperationException && !(e is SessionBusyException) && MentionsClosedConnection(e.Message))
                    return true;
                if (DbProviders.All.Any(p => p.IsBroken(e)))
                    return true;
            }
            return false;
        }

        /// <summary>ODP.NET GetDataTypeName(OracleDbType 이름) → Oracle SQL 형식 이름. NUMBER는 정밀도에 따라 Int16·Single 등으로 나오므로 되돌린다.</summary>
        internal static string OracleTypeName(string providerTypeName)
        {
            switch (providerTypeName)
            {
                case "Byte":
                case "Int16":
                case "Int32":
                case "Int64":
                case "Single":
                case "Double":
                case "Decimal":
                    return "NUMBER";
                case "BinaryFloat":
                    return "BINARY_FLOAT";
                case "BinaryDouble":
                    return "BINARY_DOUBLE";
                case "Varchar2":
                    return "VARCHAR2";
                case "NVarchar2":
                    return "NVARCHAR2";
                case "Char":
                    return "CHAR";
                case "NChar":
                    return "NCHAR";
                case "Long":
                    return "LONG";
                case "Raw":
                    return "RAW";
                case "LongRaw":
                    return "LONG RAW";
                case "Date":
                    return "DATE";
                case "TimeStamp":
                    return "TIMESTAMP";
                case "TimeStampTZ":
                    return "TIMESTAMP WITH TIME ZONE";
                case "TimeStampLTZ":
                    return "TIMESTAMP WITH LOCAL TIME ZONE";
                case "IntervalDS":
                    return "INTERVAL DAY TO SECOND";
                case "IntervalYM":
                    return "INTERVAL YEAR TO MONTH";
                case "Clob":
                    return "CLOB";
                case "NClob":
                    return "NCLOB";
                case "Blob":
                    return "BLOB";
                case "BFile":
                    return "BFILE";
                case "XmlType":
                    return "XMLTYPE";
                case "Json":
                    return "JSON";
                case "Boolean":
                    return "BOOLEAN";
                case "RefCursor":
                    return "REF CURSOR";
                default:
                    // VECTOR(…)·사용자 정의 형식 등은 공급자가 준 이름 그대로
                    return providerTypeName;
            }
        }

        /// <summary>
        /// Oracle 열 정보 보정: ROWID 열은 "ROWID"로. 정밀도 없는 NUMBER·FLOAT는 소수 자릿수가 ±127(OCI의 "정하지 않음")로 오므로
        /// 정밀도·소수 자릿수를 없앤다(NUMBER(38,127)처럼 보이지 않게).
        /// </summary>
        internal static string NormalizeOracleColumn(string providerTypeName, bool isRowId, ref int? precision, ref int? scale)
        {
            if (isRowId)
                return "ROWID";
            var typeName = OracleTypeName(providerTypeName);
            if (typeName == "NUMBER" && (scale == 127 || scale == -127))
            {
                precision = null;
                scale = null;
            }
            return typeName;
        }

        private ExecuteResult Execute(SqlStatement statement, int fetchCount)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = new ExecuteResult { Kind = statement.Kind };
            ClearWarnings();
            if (statement.TransactionAction == SqlTransactionAction.Commit)
            {
                CommitCore();
                result.TransactionEnded = true;
                result.Summary = "커밋함";
            }
            else if (statement.TransactionAction == SqlTransactionAction.Rollback)
            {
                RollbackCore();
                result.TransactionEnded = true;
                result.Summary = "롤백함";
            }
            else if (statement.Kind == SqlKind.Transaction || statement.TransactionAction == SqlTransactionAction.Other)
            {
                // SAVEPOINT, ROLLBACK TO SAVEPOINT, SET TRANSACTION, SET CONSTRAINTS: 트랜잭션 안에서만 뜻이 있다
                EnsureTransaction();
                ExecuteNonQuery(statement.Text);
                // ROLLBACK TO는 일부 변경만 되돌리므로 남은 대기 행 수를 알 수 없다
                if (_hasPending && string.Equals(statement.Verb, "ROLLBACK", StringComparison.OrdinalIgnoreCase))
                    _countUnknown = true;
                result.Summary = "실행함";
            }
            else
            {
                switch (statement.Kind)
                {
                    case SqlKind.Query:
                        ExecuteQuery(statement, fetchCount, result);
                        break;
                    case SqlKind.Dml:
                        ExecuteDml(statement, result);
                        break;
                    case SqlKind.PlSql:
                        ExecutePlSql(statement, result);
                        break;
                    case SqlKind.Ddl:
                        if (_transaction != null)
                            CommitCore();
                        ExecuteNonQuery(statement.Text);
                        EndTransaction();
                        result.TransactionEnded = true;
                        result.Summary = "실행함 (DDL은 자동 커밋됨)";
                        if (TakeWarning(CompileErrorWarning))
                        {
                            // 예외가 없어도 멀쩡하던 객체가 쓸 수 없는(INVALID) 것으로 바뀌었다
                            result.Summary = "실행함 — 컴파일 오류 (DDL은 자동 커밋됨)";
                            result.Warning = (string.Equals(statement.Verb, "ALTER", StringComparison.Ordinal) ? "다시 컴파일했지만 오류가 있습니다" : "만들었지만 컴파일 오류가 있습니다")
                                + " (ORA-24344). 객체는 고칠 때까지 쓸 수 없습니다(INVALID).";
                            ReadCompileErrors(statement, result);
                        }
                        break;
                    default:
                        if (string.Equals(statement.Verb, "LOCK", StringComparison.Ordinal))
                        {
                            // LOCK TABLE은 트랜잭션이 끝날 때 풀린다 — 트랜잭션 밖에서 실행하면 그 자리에서 커밋되어 바로 풀린다
                            EnsureTransaction();
                            ExecuteNonQuery(statement.Text);
                            _hasPending = true;
                            _locksHeld = true;
                            _tableLocked = true;
                            result.Summary = "실행함 (잠금 보유 — 커밋·롤백하면 풀림)";
                            break;
                        }
                        // ALTER SESSION 등: 열린 트랜잭션이 있으면 그 안에서 실행하고 대기 상태는 바꾸지 않는다
                        ExecuteNonQuery(statement.Text);
                        result.Summary = "실행함";
                        break;
                }
            }
            result.Elapsed = stopwatch.Elapsed;
            return result;
        }

        private void ExecuteQuery(SqlStatement statement, int fetchCount, ExecuteResult result)
        {
            // FOR UPDATE는 트랜잭션 밖이면 자동 커밋으로 잠금이 바로 풀리고 이어 읽기가 실패한다
            if (statement.ForUpdate)
                EnsureTransaction();
            var cursor = new QueryCursor { Owner = this, ForUpdate = statement.ForUpdate };
            cursor.Command = CreateCommand(statement.Text);
            try
            {
                SetExecuting(cursor.Command);
                try
                {
                    cursor.Reader = cursor.Command.ExecuteReader();
                    DescribeColumns(cursor);
                    result.Rows = ReadRows(cursor, fetchCount);
                }
                finally
                {
                    SetExecuting(null);
                }
            }
            catch
            {
                CloseCursorCore(cursor);
                throw;
            }
            if (statement.ForUpdate)
            {
                _hasPending = true;
                _locksHeld = true;
            }
            if (!cursor.IsClosed)
            {
                lock (_cursors)
                    _cursors.Add(cursor);
            }
            result.Cursor = cursor;
            result.Summary = result.Rows.Count.ToString(CultureInfo.InvariantCulture) + "행" + (cursor.HasMore ? " · 더 있음" : "");
        }

        private void ExecuteDml(SqlStatement statement, ExecuteResult result)
        {
            EnsureTransaction();
            var affected = ExecuteNonQuery(statement.Text);
            result.RecordsAffected = affected;
            _hasPending = true;
            if (affected >= 0)
            {
                _pendingRows = (int)Math.Min(int.MaxValue, (long)_pendingRows + affected);
                result.Summary = affected.ToString(CultureInfo.InvariantCulture) + "행 변경됨 (커밋 전)";
            }
            else
            {
                _countUnknown = true;
                result.Summary = "실행함 (커밋 전)";
            }
        }

        private void ExecutePlSql(SqlStatement statement, ExecuteResult result)
        {
            var wasPending = _hasPending;
            EnsureTransaction();
            try
            {
                ExecuteNonQuery(statement.Text);
            }
            catch (Exception ex)
            {
                // 커밋한 뒤 오류를 낸 블록도 앞서 대기하던 변경을 이미 끝냈다(대기 표시만 남지 않게)
                NoteFailure(ex);
                if (_hasPending && !IsCancellation(ex) && TransactionAfterBlock() == false)
                    EndTransaction();
                throw;
            }
            // 블록 안의 COMMIT·ROLLBACK·EXECUTE IMMEDIATE DDL은 앞서 대기하던 변경까지 끝낸다. 무엇을 했는지는 서버에 물어야 안다.
            if (TransactionAfterBlock() == false)
            {
                EndTransaction();
                result.TransactionEnded = true;
                result.Summary = wasPending ? "실행함 (블록 안에서 커밋·롤백됨)" : "실행함";
                return;
            }
            // 트랜잭션이 남았거나 물을 수 없으면 대기로 보고, 블록이 바꾼 행 수는 모른다고 표시한다
            _hasPending = true;
            _countUnknown = true;
            result.Summary = "실행함 (커밋 전)";
        }

        /// <summary>
        /// PL/SQL 블록 뒤 서버 트랜잭션(<see cref="ServerTransactionActive"/>). LOCK TABLE 잠금이 있으면 묻지 않고 모름(null):
        /// 그 잠금은 트랜잭션 ID가 없어 확인 값이 NULL이므로 블록이 커밋해 풀었는지 그대로인지 가릴 수 없다.
        /// </summary>
        private bool? TransactionAfterBlock()
        {
            return _tableLocked ? null : ServerTransactionActive();
        }

        /// <summary>서버에 열린 트랜잭션이 있으면 true, 없으면 false, 알 수 없으면(묻는 문장 없음·끊김·실패·취소) null.</summary>
        private bool? ServerTransactionActive()
        {
            var sql = TransactionProbeSql;
            if (string.IsNullOrEmpty(sql) || _broken)
                return null;
            try
            {
                using (var command = CreateCommand(sql))
                {
                    SetExecuting(command);
                    try
                    {
                        var value = command.ExecuteScalar();
                        return value != null && !(value is DBNull);
                    }
                    finally
                    {
                        SetExecuting(null);
                    }
                }
            }
            catch (Exception ex)
            {
                NoteFailure(ex);
                return null;
            }
        }

        private void Connection_InfoMessage(object sender, OracleInfoMessageEventArgs e)
        {
            try
            {
                foreach (OracleError error in e.Errors)
                    NoteWarning(error.Number);
            }
            catch (Exception)
            {
                // 경고를 읽지 못해도 실행 결과는 그대로다(ODP.NET도 처리기 예외를 삼킨다)
            }
        }

        /// <summary>지금 실행 중인 문장이 경고를 받았다(InfoMessage 처리기, 시험).</summary>
        internal void NoteWarning(int number)
        {
            lock (_warnings)
            {
                // 메타데이터 조회 등 실행 밖의 경고가 끝없이 쌓이지 않게
                if (_warnings.Count < 100)
                    _warnings.Add(number);
            }
        }

        private void ClearWarnings()
        {
            lock (_warnings)
                _warnings.Clear();
        }

        private bool TakeWarning(int number)
        {
            lock (_warnings)
                return _warnings.Remove(number);
        }

        /// <summary>컴파일 오류 목록(ALL_ERRORS)을 result.WarningDetails에 넣는다. 못 읽어도 경고는 그대로 둔다.</summary>
        private void ReadCompileErrors(SqlStatement statement, ExecuteResult result)
        {
            var target = SqlScript.CompileTargetOf(statement.Text);
            if (target == null || _broken)
                return;
            try
            {
                var withType = target.Types.Count > 1;
                var lines = QueryRows(OracleMetadata.CompileErrors(target.Owner, target.Name, target.Types, OracleMetadata.CompileErrorLimit),
                    r => OracleMetadata.ReadCompileError(r, withType), _operationToken);
                if (lines.Count > OracleMetadata.CompileErrorLimit)
                {
                    lines.RemoveRange(OracleMetadata.CompileErrorLimit, lines.Count - OracleMetadata.CompileErrorLimit);
                    lines.Add("… 오류가 더 있습니다(ALL_ERRORS에서 보세요).");
                }
                result.WarningDetails.AddRange(lines);
            }
            catch (Exception ex)
            {
                NoteFailure(ex);
            }
        }

        private List<string[]> FetchCore(QueryCursor cursor, int count)
        {
            if (cursor.IsClosed)
                return new List<string[]>();
            try
            {
                SetExecuting(cursor.Command);
                try
                {
                    return ReadRows(cursor, count);
                }
                finally
                {
                    SetExecuting(null);
                }
            }
            catch
            {
                // 취소·오류 뒤의 리더는 이어 읽을 수 없다(ORA-01013 뒤 ORA-01002 등)
                CloseCursorCore(cursor);
                throw;
            }
        }

        /// <summary>count행을 읽고 한 행을 더 읽어 둔다(있으면 HasMore). 끝에 닿으면 커서를 닫는다.</summary>
        private List<string[]> ReadRows(QueryCursor cursor, int count)
        {
            var rows = new List<string[]>(Math.Min(count, 1000) + 1);
            if (count > 0 && cursor.Lookahead != null)
            {
                rows.Add(cursor.Lookahead);
                cursor.Lookahead = null;
            }
            var reader = cursor.Reader;
            var exhausted = false;
            while (rows.Count < count)
            {
                if (!reader.Read())
                {
                    exhausted = true;
                    break;
                }
                rows.Add(ReadRow(reader, cursor.ReadModes));
            }
            if (!exhausted && cursor.Lookahead == null)
            {
                if (reader.Read())
                    cursor.Lookahead = ReadRow(reader, cursor.ReadModes);
                else
                    exhausted = true;
            }
            cursor.Fetched += rows.Count;
            cursor.HasMore = !exhausted;
            if (exhausted)
                CloseCursorCore(cursor);
            return rows;
        }

        private static string[] ReadRow(DbDataReader reader, ColumnRead[] modes)
        {
            var row = new string[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                var value = ReadValue(reader, i, modes != null ? modes[i] : ColumnRead.Value);
                try
                {
                    row[i] = ValueFormatter.Format(value);
                }
                finally
                {
                    DisposeValue(value);
                }
            }
            return row;
        }

        internal static object ReadValue(DbDataReader reader, int ordinal, ColumnRead mode)
        {
            switch (mode)
            {
                case ColumnRead.ProviderSpecific:
                    return ProviderValue(reader, ordinal);
                case ColumnRead.Text:
                    return TextValue(reader, ordinal);
                default:
                    return reader.GetValue(ordinal);
            }
        }

        private static object ProviderValue(DbDataReader reader, int ordinal)
        {
            try
            {
                return reader.GetProviderSpecificValue(ordinal);
            }
            catch (Exception ex) when (ex is InvalidCastException || ex is OverflowException || ex is NotSupportedException || ex is FormatException)
            {
                // Oracle 형식으로 바꿀 수 없는 값은 .NET 값으로라도 보인다
                return reader.GetValue(ordinal);
            }
            catch (Exception ex) when (IsUnreadableValue(ex))
            {
                // 형식 매핑이 없는 객체 형식(ORA-50071)·지원하지 않는 형식: GetValue도 같은 길로 실패하므로 글자로 읽는다
                return TextValue(reader, ordinal);
            }
        }

        /// <summary>GetString으로 읽는다(객체·컬렉션은 JSON, REF는 16진수). 그것도 못 읽으면 자리 표시 — 한 칸 때문에 조회 전체를 실패시키지 않는다.</summary>
        private static object TextValue(DbDataReader reader, int ordinal)
        {
            try
            {
                return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
            }
            catch (Exception ex) when (IsUnreadableValue(ex) || ex is InvalidCastException || ex is NotSupportedException || ex is FormatException)
            {
                return UnreadableValue;
            }
        }

        // 값 하나를 읽지 못한 것뿐인 오류(연결 끊김·취소는 아님 — 그런 오류는 조회를 멈춰야 한다)
        private static bool IsUnreadableValue(Exception ex)
        {
            return (ex is InvalidOperationException || ex is OracleException) && !IsBrokenError(ex) && !IsCancellation(ex);
        }

        private static void DisposeValue(object value)
        {
            // Null 값은 공유 정적 인스턴스(OracleClob.Null 등)라 건드리지 않는다
            if (value is Oracle.ManagedDataAccess.Types.INullable nullable && nullable.IsNull)
                return;
            var disposable = value as IDisposable;
            if (disposable == null)
                return;
            try
            {
                disposable.Dispose();
            }
            catch (Exception)
            {
                // 이미 형식화한 값의 정리 실패로 조회 전체를 실패시키지 않는다
            }
        }

        private static void DescribeColumns(QueryCursor cursor)
        {
            var reader = cursor.Reader;
            var count = reader.FieldCount;
            var schema = SchemaRows(reader, count);
            var oracle = reader is OracleDataReader;
            if (oracle)
                cursor.ReadModes = new ColumnRead[count];
            for (var i = 0; i < count; i++)
            {
                var row = schema != null ? schema[i] : null;
                var size = SchemaInt(row, SchemaTableColumn.ColumnSize);
                var precision = SchemaInt(row, SchemaTableColumn.NumericPrecision);
                var scale = SchemaInt(row, SchemaTableColumn.NumericScale);
                var typeName = reader.GetDataTypeName(i);
                if (oracle)
                {
                    cursor.ReadModes[i] = OracleReadMode(typeName);
                    typeName = NormalizeOracleColumn(typeName, SchemaInt(row, "IsRowID") == 1, ref precision, ref scale);
                }
                cursor.Columns.Add(ValueFormatter.DescribeColumn(reader.GetName(i), typeName, reader.GetFieldType(i), size, precision, scale));
            }
        }

        /// <summary>ODP.NET GetDataTypeName(OracleDbType 이름)으로 정하는 열 읽기 방법.</summary>
        internal static ColumnRead OracleReadMode(string providerTypeName)
        {
            switch (providerTypeName)
            {
                case "BinaryFloat":
                case "BinaryDouble":
                    // Oracle 형식(OracleDecimal)으로 바꾸면 자릿수를 잃고 큰 값은 넘친다
                    return ColumnRead.Value;
                case "Object":
                case "Array":
                case "Ref":
                    // 사용자 정의 형식(SDO_GEOMETRY·ANYDATA·VARRAY 등)과 REF는 형식 매핑이 없으면 GetProviderSpecificValue·GetValue가 예외를 낸다
                    return ColumnRead.Text;
                default:
                    return ColumnRead.ProviderSpecific;
            }
        }

        /// <summary>
        /// 열 크기·정밀도는 GetSchemaTable에서 읽는다. GetColumnSchema()는 NumericPrecision·NumericScale이 Int16인 표(ODP.NET, SQLite)에서
        /// 값을 버리므로(int?로 형 변환 실패) 쓰지 않는다. 표시용이라 읽지 못해도 조회는 계속한다.
        /// </summary>
        private static DataRow[] SchemaRows(DbDataReader reader, int count)
        {
            try
            {
                var table = reader.GetSchemaTable();
                if (table == null || table.Rows.Count != count)
                    return null;
                var rows = new DataRow[count];
                table.Rows.CopyTo(rows, 0);
                return rows;
            }
            catch (Exception ex) when (!(ex is DbException))
            {
                return null;
            }
        }

        internal static int? SchemaInt(DataRow row, string column)
        {
            if (row == null || !row.Table.Columns.Contains(column))
                return null;
            var value = row[column];
            if (value == null || value is DBNull)
                return null;
            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is InvalidCastException || ex is OverflowException || ex is FormatException)
            {
                return null;
            }
        }

        private DbCommand CreateCommand(string text)
        {
            ReleaseReadersIfSingle();
            var command = _connection.CreateCommand();
            command.CommandText = text;
            // 연결에 트랜잭션이 있으면 명령에도 넣어야 실행하는 공급자가 있다(CreateCommand가 넣어 주지 않는 경우)
            if (_transaction != null)
                command.Transaction = _transaction;
            var oracle = command as OracleCommand;
            if (oracle != null)
            {
                oracle.BindByName = true;
                oracle.InitialLOBFetchSize = ValueFormatter.MaxTextLength;
                // LONG 열(ALL_VIEWS.TEXT 등)은 기본값 0이면 select 목록에 ROWID·기본 키가 없을 때 읽을 수 없다
                oracle.InitialLONGFetchSize = -1;
                // 시간 제한 없음(사용자가 Cancel로 멈춘다). SQLite는 Cancel이 동작하지 않고 0이 잠금 무한 대기라 공급자 기본값을 둔다.
                oracle.CommandTimeout = 0;
            }
            return command;
        }

        private int ExecuteNonQuery(string text)
        {
            using (var command = CreateCommand(text))
            {
                SetExecuting(command);
                try
                {
                    return command.ExecuteNonQuery();
                }
                finally
                {
                    SetExecuting(null);
                }
            }
        }

        private void SetExecuting(DbCommand command)
        {
            lock (_executingSync)
                _executing = command;
            if (command == null || !_operationToken.IsCancellationRequested)
                return;
            // 명령을 등록하기 전에 들어온 취소는 Cancel()이 놓쳤으므로 실행하지 않고 여기서 멈춘다(예: DDL 앞 커밋 중 취소)
            lock (_executingSync)
                _executing = null;
            throw new OperationCanceledException(_operationToken);
        }

        private void EnsureTransaction()
        {
            if (_transaction == null)
            {
                ReleaseReadersIfSingle();
                _transaction = _connection.BeginTransaction();
            }
        }

        /// <summary>
        /// 리더를 하나만 열 수 있는 공급자(PostgreSQL)면 열린 결과 커서를 모두 닫는다 — 새 명령·트랜잭션 시작·커밋·롤백 전에 부른다.
        /// 닫힌 커서는 HasMore = false·IsClosed = true가 되어 화면의 [다음 행 가져오기]가 꺼진다(가져온 행은 그대로).
        /// 잠금 안(작업 스레드)에서만 부른다.
        /// </summary>
        private void ReleaseReadersIfSingle()
        {
            if (Provider.SupportsMultipleOpenReaders)
                return;
            foreach (var cursor in SnapshotCursors())
                CloseCursorCore(cursor);
        }

        private void CommitCore()
        {
            // 트랜잭션 객체가 없어도 서버 트랜잭션은 있을 수 있다(자동 커밋을 끈 ODP.NET 연결의 DB 링크 조회·EXPLAIN PLAN 등) — 언제나 서버까지 보낸다
            ReleaseReadersIfSingle();
            EnsureTransaction();
            var transaction = _transaction;
            try
            {
                transaction.Commit();
            }
            catch (Exception ex)
            {
                if (AfterFailedEnd(transaction, ex))
                    throw new TransactionEndedException(ex);
                throw;
            }
            _transaction = null;
            DisposeQuietly(transaction);
            EndTransaction();
        }

        private void RollbackCore()
        {
            // CommitCore와 같은 까닭으로 트랜잭션 객체가 없어도 서버까지 롤백한다
            ReleaseReadersIfSingle();
            EnsureTransaction();
            var transaction = _transaction;
            try
            {
                transaction.Rollback();
            }
            catch (Exception ex)
            {
                if (AfterFailedEnd(transaction, ex))
                    throw new TransactionEndedException(ex);
                throw;
            }
            _transaction = null;
            DisposeQuietly(transaction);
            EndTransaction();
        }

        /// <summary>
        /// 커밋·롤백이 실패한 뒤 트랜잭션 상태를 맞춘다. 실패해도 ODP.NET은 트랜잭션 객체를 끝난 것으로 만든다 — 그대로 쥐고 있으면
        /// 그 뒤 명령이 트랜잭션 없이 실행되고(자동 커밋) 다시 커밋·롤백할 수도 없다. 그런 객체는 버리고, 서버에 트랜잭션이 남았으면
        /// (또는 알 수 없으면) 새 객체로 이어 받는다(대기 상태 유지). 서버에서 트랜잭션이 끝났으면(ORA-02091 롤백 등) 대기 상태를 지우고 true.
        /// </summary>
        private bool AfterFailedEnd(DbTransaction transaction, Exception failure)
        {
            NoteFailure(failure);
            // 일반 ADO.NET 공급자는 실패한 트랜잭션을 살려 두므로(Connection이 그대로) 다시 커밋하거나 롤백할 수 있다
            if (!(transaction is OracleTransaction) && transaction.Connection != null)
                return false;
            _transaction = null;
            DisposeQuietly(transaction);
            if (_broken)
                return false;
            if (ServerTransactionActive() == false)
            {
                EndTransaction();
                return true;
            }
            try
            {
                _transaction = _connection.BeginTransaction();
            }
            catch (Exception ex)
            {
                // 다음 DML 앞의 EnsureTransaction이 다시 연다. 원래 오류를 가리지 않는다
                NoteFailure(ex);
            }
            return false;
        }

        /// <summary>트랜잭션이 끝난 뒤: 대기 상태를 지우고, 더 읽을 수 없게 된 FOR UPDATE 커서를 닫는다.</summary>
        private void EndTransaction()
        {
            _hasPending = false;
            _countUnknown = false;
            _locksHeld = false;
            _tableLocked = false;
            _pendingRows = 0;
            foreach (var cursor in SnapshotCursors())
            {
                if (cursor.ForUpdate)
                    CloseCursorCore(cursor);
            }
        }

        private void CloseCore(bool commit)
        {
            _closed = true;
            // Dispose와 같은 순서(커서 → 커밋·롤백 → 연결). 커밋·롤백이 실패해도 나머지는 닫는다.
            CloseCursors();
            Exception failure = null;
            var transaction = _transaction;
            _transaction = null;
            if (transaction != null)
            {
                try
                {
                    if (commit)
                        transaction.Commit();
                    else
                        transaction.Rollback();
                }
                catch (Exception ex)
                {
                    NoteFailure(ex);
                    if (commit || !_broken)
                        failure = ex;
                }
                DisposeQuietly(transaction);
            }
            CloseConnection();
            if (failure != null)
                ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private void CloseCursors()
        {
            foreach (var cursor in SnapshotCursors())
                CloseCursorCore(cursor);
        }

        private void CloseConnection()
        {
            _hasPending = false;
            _countUnknown = false;
            _locksHeld = false;
            _tableLocked = false;
            _pendingRows = 0;
            var oracle = _connection as OracleConnection;
            if (oracle != null)
                oracle.InfoMessage -= Connection_InfoMessage;
            try
            {
                _connection.Close();
            }
            catch (Exception)
            {
                // 끊긴 연결은 닫기도 실패할 수 있다
            }
            DisposeQuietly(_connection);
        }

        private void CloseCursorCore(QueryCursor cursor)
        {
            lock (_cursors)
                _cursors.Remove(cursor);
            cursor.HasMore = false;
            cursor.IsClosed = true;
            cursor.Lookahead = null;
            var reader = cursor.Reader;
            var command = cursor.Command;
            cursor.Reader = null;
            cursor.Command = null;
            // 끝까지 읽어 닫을 때는 아직 실행 중으로 등록되어 있다. Dispose한 명령을 Cancel()이 건드리지 않게 먼저 뺀다.
            lock (_executingSync)
            {
                if (command != null && ReferenceEquals(_executing, command))
                    _executing = null;
            }
            DisposeQuietly(reader);
            DisposeQuietly(command);
        }

        private List<QueryCursor> SnapshotCursors()
        {
            lock (_cursors)
                return new List<QueryCursor>(_cursors);
        }

        private void DisposeQuietly(IDisposable item)
        {
            if (item == null)
                return;
            try
            {
                item.Dispose();
            }
            catch (Exception ex)
            {
                // 닫기 실패는 닫힌 것으로 본다. 연결이 끊겨서라면 표시만 남긴다
                NoteFailure(ex);
            }
        }

        private Task<T> RunDb<T>(Func<T> work, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                _operationToken = cancellationToken;
                try
                {
                    using (cancellationToken.Register(Cancel))
                        return work();
                }
                catch (Exception ex)
                {
                    NoteFailure(ex);
                    throw;
                }
                finally
                {
                    _operationToken = CancellationToken.None;
                }
            }, cancellationToken);
        }

        private void NoteFailure(Exception ex)
        {
            if (IsBrokenError(ex) || (_opened && !ConnectionIsOpen()))
                _broken = true;
        }

        private bool ConnectionIsOpen()
        {
            try
            {
                return _connection.State == ConnectionState.Open;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>잠금을 바로 얻는다. 못 얻으면 SessionBusyException.</summary>
        private void EnterNow()
        {
            ThrowIfNotReady();
            if (!_lock.Wait(0))
                throw new SessionBusyException();
            if (_closed)
            {
                _lock.Release();
                ThrowIfClosed();
            }
        }

        private void ThrowIfNotReady()
        {
            ThrowIfClosed();
            if (!_opened)
                throw new InvalidOperationException("DB에 연결되어 있지 않습니다. 먼저 연결하세요.");
        }

        private void ThrowIfClosed()
        {
            if (_closed)
                throw new InvalidOperationException("DB 세션이 닫혔습니다. 다시 연결하세요.");
        }

        private void ThrowIfForeign(QueryCursor cursor)
        {
            if (cursor.Owner != this)
                throw new ArgumentException("이 세션에서 연 커서가 아닙니다.", nameof(cursor));
        }

        private static bool EndsTransaction(SqlStatement statement)
        {
            return statement.TransactionAction == SqlTransactionAction.Commit || statement.TransactionAction == SqlTransactionAction.Rollback;
        }

        private static Exception Unwrap(Exception ex)
        {
            // 중첩된 AggregateException·TargetInvocationException을 끝까지 벗긴다(순환 방지로 횟수 제한)
            for (var i = 0; i < 16 && ex != null; i++)
            {
                if (ex is AggregateException aggregate)
                {
                    var inner = aggregate.Flatten().InnerExceptions;
                    if (inner.Count == 0)
                        return ex;
                    ex = inner[0];
                }
                else if (ex is TargetInvocationException invocation && invocation.InnerException != null)
                {
                    ex = invocation.InnerException;
                }
                else
                {
                    return ex;
                }
            }
            return ex;
        }

        /// <summary>
        /// 예외 사슬에서 연결 끊김 번호를 가진 OracleException의 Number. 메시지 속 번호는 보지 않는다
        /// (DB 링크 오류 "ORA-02068 … ORA-03113"처럼 이 연결과 무관한 번호가 메시지에 섞일 수 있다).
        /// </summary>
        private static int? BrokenErrorNumber(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is OracleException oracle && BrokenErrorNumbers.Contains(oracle.Number))
                    return oracle.Number;
            }
            return null;
        }

        private static bool MentionsClosedConnection(string message)
        {
            if (string.IsNullOrEmpty(message))
                return false;
            foreach (var phrase in ClosedConnectionPhrases)
            {
                if (message.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static string OracleCode(int number)
        {
            return "ORA-" + number.ToString("D5", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// ODP.NET의 연결 실패는 겉 오류가 ORA-50201(서버에 연결하지 못했거나 연결 문자열을 해석하지 못함)뿐이고
        /// 실제 원인(ORA-12514 리스너가 서비스를 모름, ORA-12541 리스너 없음 등)은 InnerException 안쪽(NetworkException)에 있다.
        /// 겉 줄이 오류 번호로 시작하고 안쪽에 다른 번호가 있으면 가장 깊은 것을 앞에 두고 겉 번호는 괄호로 남긴다.
        /// </summary>
        private static string WithInnerCause(Exception ex, string first)
        {
            var outer = ErrorCodeOf(first);
            if (outer == null)
                return first;
            string cause = null;
            var e = ex.InnerException;
            for (var i = 0; i < 16 && e != null; i++, e = e.InnerException)
            {
                var line = FirstLine(e);
                var code = ErrorCodeOf(line);
                if (code != null && code != outer)
                    cause = line;
            }
            return cause == null ? first : cause + " (" + outer + ")";
        }

        /// <summary>"ORA-12514: …"·"TNS-12541: …" → "ORA-12514"·"TNS-12541". 오류 번호로 시작하지 않으면 null.</summary>
        private static string ErrorCodeOf(string line)
        {
            if (line == null || line.Length < 9 || !(line.StartsWith("ORA-", StringComparison.Ordinal) || line.StartsWith("TNS-", StringComparison.Ordinal)))
                return null;
            for (var i = 4; i < 9; i++)
            {
                if (line[i] < '0' || line[i] > '9')
                    return null;
            }
            return line.Substring(0, 9);
        }

        private static string FirstLine(Exception ex)
        {
            var lines = new List<string>();
            foreach (var line in (ex.Message ?? "").Split('\n'))
            {
                var text = line.Trim();
                // 23ai의 ODP.NET은 둘째 줄에 오류 도움말 주소를 붙인다
                if (text.Length > 0 && !text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    lines.Add(text);
            }
            if (lines.Count == 0)
                return ex.GetType().Name;
            var first = lines[0];
            var oracle = ex as OracleException;
            // "ORA-06550: line 1, column 7:"처럼 ':'로 끝나면 실제 원인(PLS-00201 …)은 다음 줄에 있다
            for (var i = 1; i < lines.Count && i < 3 && first.EndsWith(":", StringComparison.Ordinal); i++)
                first += " " + lines[i];
            // ORA-02091(커밋할 때 트랜잭션이 롤백됨)도 원인(지연 제약 위반 ORA-02291 등)이 다음 줄에 있다
            if (lines.Count > 1 && first == lines[0] && ((oracle != null && oracle.Number == 2091) || first.StartsWith("ORA-02091", StringComparison.Ordinal)))
                first += " " + lines[1];
            if (oracle != null && oracle.Number > 0 && !first.StartsWith("ORA-", StringComparison.Ordinal))
                first = OracleCode(oracle.Number) + ": " + first;
            return first;
        }
    }
}

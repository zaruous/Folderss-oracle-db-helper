-- GCM(공통 코드) 조회 본보기 — PostgreSQL, 추가 속성을 JSON 객체 열 하나로 모으는 꼴.
-- [데이터 테이블]·[정의 테이블]을 실제 테이블 이름으로 바꾸세요. 열 이름은 범용 코드 테이블(정의 테이블이 열 이름, 데이터 테이블이 값)의 본보기입니다.
-- AS 뒤의 별칭이 계약입니다 — 필수: TABLE_NAME, CODE, CODE_NAME · 선택: GROUP_NAME, GROUP_DESC, CODE_DESC, SORT_NO, USE_YN · 그 밖의 열은 추가 속성.
--
-- ATTRS 열: 비어 있지 않은 값이 모두 {"이름": 값, …} 꼴이면 플러그인이 이 열 대신 키 하나하나를 추가 속성으로 펼칩니다(값이 null인 키는 뺌).
--   이름은 정의 테이블의 *_NAME 값, 없으면(정의 행이 없거나 이름이 NULL) 열 이름(KEY_1, DATA_2 …)으로 — 이름/값 짝 열 방식과 같은 규칙.
--   jsonb_build_object는 키가 NULL이면 오류이므로 COALESCE로 열 이름을 넣고, 값이 NULL인 키는 jsonb_strip_nulls로 지웁니다.
--   같은 결과를 DBMS 공통 SQL로 내려면 지침 3장의 이름/값 짝 열 방식(KEY_1_NAME/KEY_1 …)을 쓰세요 — 이 꼴은 PostgreSQL 전용 함수를 씁니다.
-- 공장마다 같은 그룹이 되풀이되므로 공장 하나만 고릅니다(아래 WHERE — 기본은 첫 공장, 'MRC'처럼 직접 적어도 됩니다).
SELECT dat.table_name AS TABLE_NAME,
       def.table_desc AS GROUP_NAME,
       dat.key_1
         || CASE WHEN dat.key_2 IS NULL OR dat.key_2 = 'NONE' THEN '' ELSE '|' || dat.key_2 END
         || CASE WHEN dat.key_3 IS NULL OR dat.key_3 = 'NONE' THEN '' ELSE '|' || dat.key_3 END
         || CASE WHEN dat.key_4 IS NULL OR dat.key_4 = 'NONE' THEN '' ELSE '|' || dat.key_4 END
         || CASE WHEN dat.key_5 IS NULL OR dat.key_5 = 'NONE' THEN '' ELSE '|' || dat.key_5 END AS CODE,
       dat.key_desc AS CODE_NAME,
       dat.seq_no   AS SORT_NO,
       CASE WHEN COALESCE(dat.disable_flag, 0) = 0 THEN 'Y' ELSE 'N' END AS USE_YN,
       -- 추가 속성 전부를 JSON 객체 하나로: 키 = 정의 테이블의 이름(없으면 열 이름), 값 = 데이터 테이블의 값('NONE'은 빈 값으로)
       jsonb_strip_nulls(jsonb_build_object(
         COALESCE(def.key_1_name,   'KEY_1'),   NULLIF(dat.key_1, 'NONE'),
         COALESCE(def.key_2_name,   'KEY_2'),   NULLIF(dat.key_2, 'NONE'),
         COALESCE(def.key_3_name,   'KEY_3'),   NULLIF(dat.key_3, 'NONE'),
         COALESCE(def.key_4_name,   'KEY_4'),   NULLIF(dat.key_4, 'NONE'),
         COALESCE(def.key_5_name,   'KEY_5'),   NULLIF(dat.key_5, 'NONE'),
         COALESCE(def.data_1_name,  'DATA_1'),  dat.data_1,
         COALESCE(def.data_2_name,  'DATA_2'),  dat.data_2,
         COALESCE(def.data_3_name,  'DATA_3'),  dat.data_3,
         COALESCE(def.data_4_name,  'DATA_4'),  dat.data_4,
         COALESCE(def.data_5_name,  'DATA_5'),  dat.data_5,
         COALESCE(def.data_6_name,  'DATA_6'),  dat.data_6,
         COALESCE(def.data_7_name,  'DATA_7'),  dat.data_7,
         COALESCE(def.data_8_name,  'DATA_8'),  dat.data_8,
         COALESCE(def.data_9_name,  'DATA_9'),  dat.data_9,
         COALESCE(def.data_10_name, 'DATA_10'), dat.data_10
       ))::text AS ATTRS
  FROM [데이터 테이블] dat
  LEFT JOIN [정의 테이블] def ON def.table_name = dat.table_name   -- 정의 행이 없는 그룹도 남긴다
 WHERE dat.factory_code = (SELECT MIN(factory_code) FROM [데이터 테이블])   -- 공장 하나(첫 공장). 특정 공장은 'MRC'처럼 적습니다
 ORDER BY dat.table_name, dat.seq_no, dat.key_1

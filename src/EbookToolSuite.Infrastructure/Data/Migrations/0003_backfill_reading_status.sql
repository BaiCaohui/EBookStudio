-- 0003_backfill_reading_status.sql  回填阅读状态（T2-3 收尾）
--
-- 背景：T2-3 之前**没有任何代码维护 books.reading_status**  它只在导入时被写成"未读"，
-- 之后不管读了多少都不再变。于是按状态筛"在读"必然筛不出东西（本机实测：28 本书全是 0，
-- 其中一本进度已经 58%）。
--
-- 这里只做一次回填：有阅读进度、但状态还是"未读"的书 -> "在读"。
-- 已读完/弃读不动（那时还没有产生这两个状态的路径）。
--
-- 幂等：条件里带着 reading_status = 0，重跑结果一样。
-- 纪律：一经发布不得修改，后续演进只追加 0004_*.sql。

UPDATE books
   SET reading_status = 1
 WHERE reading_status = 0
   AND EXISTS (
       SELECT 1
         FROM reading_progress p
        WHERE p.book_id = books.id
          AND p.percent > 0
   );
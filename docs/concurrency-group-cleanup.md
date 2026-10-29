# Concurrency-group cleanup and configuration reads

Unused unconfigured groups default to 24 hours of idle retention, an hourly cleanup interval and 1,000 rows per transaction. The worker and dashboard share one cleanup service per host. PostgreSQL serializes cleanup transactions with its existing schema-scoped retention advisory lock; locked groups are skipped.

Eligibility requires zero occupancy, no retained job or schedule references, no default or override concurrency/rate configuration, and no future rate cooldown. Failed and completed job references remain until normal job retention removes them. Configured groups remain even when no scheduler job uses them. Cleanup runs independently of terminal-job cleanup, and existing rows require no migration or manual deletion.

`IConcurrencyGroupConfigurationReader.GetConfigurationAsync` reads only the group's configuration row. Missing groups return an effective limit of one without writing. External work reservations remain the application's responsibility; scheduler occupancy counts claimed scheduler jobs.

## Production read-only performance evidence

Measured 2026-10-29 against the production CMS database's read replica, in a read-only transaction with a three-second statement timeout. Twenty sequential `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` samples per query:

| Query | Median | p95 | Maximum |
| --- | ---: | ---: | ---: |
| Configuration lookup | 0.002 ms | 0.013 ms | 0.029 ms |
| Cleanup candidate selection, limit 1,000 | 0.410 ms | 0.576 ms | 0.738 ms |

The candidate query returned 410 eligible rows. These are server execution timings, not HTTP timings or a load test. The read-only candidate selection omits `FOR UPDATE`; deletion and concurrent locking behavior are covered by PostgreSQL integration tests, including 2,000 groups and competing cleanup hosts.

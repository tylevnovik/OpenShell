# 项目稳定性修复任务清单

- **创建日期**: 2026-07-18
- **关联审计**: `docs/project-stability-audit.md`
- **合规测试**: `tests/OpenShell.IntegrationTests/ProjectStabilityComplianceTests.cs`

状态标记：`[ ]` 待办 / `[~]` 进行中 / `[x]` 完成 / `[!]` 阻塞

---

| ID | 优先级 | 缺陷 | 任务 | 状态 | 依赖 | 对应测试 |
|----|--------|------|------|------|------|----------|
| T-590 | P0 | — | 建立审计、任务清单、实施计划与合规测试基线 | `[x]` | — | 全部先 Skip |
| T-500 | P1 | D-500 | 使 `InProcessEventBus.Dispose()` 线程安全且幂等 | `[x]` | T-590 | `EventBus_Dispose_IsIdempotent` |
| T-501 | P1 | D-501 | 将 `ArgumentException` 家族映射为 `InvalidArgument` | `[x]` | T-590 | `ErrorRecord_MapsArgumentException` |
| T-502 | P1 | D-502 | 修复 ISO 日期/日期时间字面量词法化 | `[x]` | T-590 | `FilterLexer_ParsesIsoDateLiteral` |
| T-503 | P1 | D-503 | 重构 Provider 取消合约测试参数与强断言 | `[x]` | T-590 | Provider contract suite |
| T-504 | P1 | D-504 | FileSystem/SFTP 公共异步 API 优先响应预取消 token | `[~]` | T-503 | `Providers_HonorPreCancelledToken` |
| T-505 | P0 | D-505 | CI SDK 升级为支持 `.slnx` 的 .NET 10.0.x | `[ ]` | T-590 | `Ci_UsesSlnxCompatibleSdk` |
| T-591 | P0 | — | 全量构建、测试、Skip 审计、CLI/GUI 烟测并回写结果 | `[ ]` | T-500~T-505 | 全解决方案 |

## 变更日志

- 2026-07-18 T-590 完成：完成现状审计；确认 2075 通过 / 7 跳过 / 0 失败，复现 .NET 8 无法构建 `.slnx`；新增合规基线 8 通过 / 6 跳过 / 0 失败。
- 2026-07-18 T-500 完成：`InProcessEventBus.Dispose()` 增加原子清理门，重复/并发释放保持幂等；Core + Integration 定向测试 2 通过。
- 2026-07-18 T-501 完成：`ErrorRecord.FromException` 将 `ArgumentException` 及派生类型统一映射为 `InvalidArgument`；定向测试 2 通过。
- 2026-07-18 T-502 完成：Lexer 在数字分支前识别并验证 ISO 日期/日期时间；`ExprParserTests` 39 通过，Integration 合规测试 1 通过。
- 2026-07-18 T-503 完成：共享 Provider 合约改用有效安全参数，完整等待三类异步返回并强制断言取消；测试已分别抓到 FileSystem/SFTP 首个违规入口。

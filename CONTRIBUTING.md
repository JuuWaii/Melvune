# 参与 Melvune

感谢试用、反馈和提交修复。请先阅读 [README](README.md)、[文档索引](docs/README.md) 与 [当前开发清单](docs/NEXT_DEVELOPMENT_CHECKLIST.md)，避免重复实现或把历史计划当作当前承诺。

## 反馈问题

在 Issues 中注明版本、入口（ZIP 或便携 EXE）、Windows 版本、音乐来源、复现步骤、预期与实际结果。音频问题请补充格式和定位位置。请勿提交个人配置、账号密码、令牌、私人服务地址、音频文件或未经审查的截图。

## 开发与检查

使用 Windows x64，执行 README 中的 bootstrap/build/test 命令。依赖版本由锁定文件与工具链清单固定；不要提交 `.tools`、`bin`、`obj`、`dist` 或 `.review-audit`。

按相关功能批量修改，完成后统一运行必要检查；修复后只重复受影响的项目。界面或真实服务行为变化应附实际验证结果，无法验证的场景明确写出「未验证」。构建成功不能替代听音、硬件和服务兼容性验收。

发布工具的离线回归：

```powershell
python -m unittest discover -s tests -p test_release_publication.py
python scripts/test-portable-verification.py
node --test tests/*.test.cjs
node scripts/check-privacy.js
```

提交应说明具体问题、修复后的行为、验证结果及剩余限制。可以使用 AI 辅助开发，请核对生成内容和依赖许可；项目的 [AI 声明](AI_DISCLOSURE.md) 同样适用。发布流程见 [RELEASING](docs/RELEASING.md)。

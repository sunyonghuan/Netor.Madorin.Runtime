# Expert JSON 输出示例（PowerShell）
param()

Write-Host "=== Expert JSON Output Example ===" -ForegroundColor Cyan

madorin run `
  --response-format json `
  --input "分析当前项目的 README.md（如果存在）或 *.csproj / package.json，提取项目名称、主要特性（3-5 条）、核心技术栈。以 JSON 返回，字段为 projectName, features[], techStack[]。"

#!/bin/bash
# 测试Meeting模式：Host委派Developer执行文件操作

set -e

echo "=== Meeting模式工具调用测试 ==="
echo ""

# 准备测试文件
TEST_DIR="/tmp/madorin-test-$(date +%s)"
mkdir -p "$TEST_DIR"
echo "Meeting test data" > "$TEST_DIR/input.txt"

echo "测试文件已创建: $TEST_DIR/input.txt"
echo ""

# 执行Meeting模式测试
echo "执行命令: madorin run --mode meeting --input \"Let's discuss the file $TEST_DIR/input.txt. Developer should read it and tell us what's inside.\""
echo ""

cd "E:\Netor.me\Madorin\Netor.Madorin.Ai.Runtime"
madorin run --mode meeting \
  --provider deepseek \
  --model deepseek-v4-flash \
  --input "Let's discuss the file $TEST_DIR/input.txt. Developer should read it and tell us what's inside."

echo ""
echo "=== 测试完成 ==="
echo "清理测试文件: $TEST_DIR"
rm -rf "$TEST_DIR"

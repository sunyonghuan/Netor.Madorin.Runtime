#!/bin/bash
# 测试Work模式：GM委派Developer读取文件

set -e

echo "=== Work模式工具调用测试 ==="
echo ""

# 准备测试文件
TEST_DIR="/tmp/madorin-test-$(date +%s)"
mkdir -p "$TEST_DIR"
echo "Hello from test file!" > "$TEST_DIR/test.txt"
echo "This is line 2" >> "$TEST_DIR/test.txt"
echo "This is line 3" >> "$TEST_DIR/test.txt"

echo "测试文件已创建: $TEST_DIR/test.txt"
echo ""

# 执行Work模式测试
echo "执行命令: madorin run --mode work --input \"Let developer read $TEST_DIR/test.txt and tell me its content\""
echo ""

cd "E:\Netor.me\Madorin\Netor.Madorin.Ai.Runtime"
madorin run --mode work \
  --provider deepseek \
  --model deepseek-v4-flash \
  --input "Let developer read $TEST_DIR/test.txt and tell me its content"

echo ""
echo "=== 测试完成 ==="
echo "清理测试文件: $TEST_DIR"
rm -rf "$TEST_DIR"

#!/usr/bin/env bash
# 上传 Release 资产。实测约束：
#   1) 单次请求约 600 秒会被重置。58.44MB 要在 595s 内传完 → 需持续 ≥103KB/s；
#   2) 链路会随机 RST（曾在 15MB/123s 断）。
# 所以：低于 103KB/s 持续 30s 就提前放弃（再耗下去也赶不上截止时间），重来；
# 每次先清掉 starter 残包，否则同名会冲突。成功那次往往只有十几秒（链路突然很通）。
set -u
cd /d/project/TaskbarIdle || exit 1

TOKEN=$(printf "protocol=https\nhost=github.com\n\n" | git credential fill 2>/dev/null | grep "^password=" | cut -d= -f2)
API=https://api.github.com/repos/charming386/KeySpirit-1
# 换版本时改这两行（REL = 新 Release 的 id，从创建 Release 的响应里取）
REL=${REL:-434956730}
NAME=${NAME:-KeySpirit-v0.12.1-win-x64.zip}
TRIES=${TRIES:-10}
FILE=release/$NAME
SIZE=$(stat -c %s "$FILE")
NEED=$(( SIZE / 595 / 1024 + 1 ))     # 595s 内传完所需的平均速度（KB/s）
echo "包大小 $(( SIZE / 1048576 )) MB，需 ≥${NEED}KB/s"

cleanup() {
  curl -s -H "Authorization: token $TOKEN" "$API/releases/$REL/assets" \
    | python -c "import json,sys
for a in json.load(sys.stdin): print(a['id'])" \
    | while read -r id; do
        curl -s -o /dev/null -X DELETE -H "Authorization: token $TOKEN" "$API/releases/assets/$id"
      done
}

for i in $(seq 1 "$TRIES"); do
  echo "=== 第 $i 次 $(date +%H:%M:%S) ==="
  cleanup
  code=$(curl -sS --http1.1 -H "Expect:" --max-time 595 \
      --speed-limit $(( NEED * 1024 )) --speed-time 30 \
      -X POST -H "Authorization: token $TOKEN" \
      -H "Content-Type: application/zip" \
      --data-binary "@$FILE" \
      "https://uploads.github.com/repos/charming386/KeySpirit-1/releases/$REL/assets?name=$NAME" \
      -o release/upload_resp.json \
      -w "%{http_code} %{speed_download}" 2>release/upload_err.txt)
  set -- $code
  echo "HTTP ${1:-000}  $(date +%H:%M:%S)  平均 $(( ${2:-0} / 1024 ))KB/s  $(tail -1 release/upload_err.txt 2>/dev/null)"
  if [ "${1:-}" = "201" ]; then
    echo "SUCCESS"
    exit 0
  fi
  sleep 3
done
echo "FAILED_ALL"

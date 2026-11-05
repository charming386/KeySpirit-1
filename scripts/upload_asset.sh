#!/usr/bin/env bash
# 上传 Release 资产。实测两个约束：
#   1) 单次请求约 600 秒会被重置，58.45MB 要在 600s 内传完 → 需持续 ≥97KB/s；
#   2) 链路会随机 RST（曾在 15MB/123s 断）。
# 所以：低于 97KB/s 持续 45s 就提前放弃，重来；每次先清掉 starter 残包。
set -u
cd /d/project/TaskbarIdle || exit 1

TOKEN=$(printf "protocol=https\nhost=github.com\n\n" | git credential fill 2>/dev/null | grep "^password=" | cut -d= -f2)
API=https://api.github.com/repos/charming386/KeySpirit-1
REL=433795269
NAME=KeySpirit-v0.12.0-win-x64.zip
FILE=release/$NAME
SIZE=$(stat -c %s "$FILE")

cleanup() {
  curl -s -H "Authorization: token $TOKEN" "$API/releases/$REL/assets" \
    | python -c "import json,sys
for a in json.load(sys.stdin): print(a['id'])" \
    | while read -r id; do
        curl -s -o /dev/null -X DELETE -H "Authorization: token $TOKEN" "$API/releases/assets/$id"
      done
}

for i in $(seq 1 8); do
  echo "=== 第 $i 次 $(date +%H:%M:%S) ==="
  cleanup
  code=$(curl -sS --http1.1 -H "Expect:" --max-time 595 \
      --speed-limit 97000 --speed-time 45 \
      -X POST -H "Authorization: token $TOKEN" \
      -H "Content-Type: application/zip" \
      --data-binary "@$FILE" \
      "https://uploads.github.com/repos/charming386/KeySpirit-1/releases/$REL/assets?name=$NAME" \
      -o release/upload_resp.json -w "%{http_code}" 2>release/upload_err.txt)
  echo "HTTP $code  $(date +%H:%M:%S)  $(tail -1 release/upload_err.txt 2>/dev/null)"
  if [ "$code" = "201" ]; then
    echo "SUCCESS"
    exit 0
  fi
  sleep 4
done
echo "FAILED_ALL"

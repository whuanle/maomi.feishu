"""生成 FeishuWss NuGet 包图标 (128x128 PNG)。

设计：圆角方形飞书蓝底 + 双向循环箭头（持久长连接的经典语义）+ 消息气泡点。
"""
from PIL import Image, ImageDraw
import os

SIZE = 128
SS = 4  # 超采样倍数，抗锯齿
W = SIZE * SS

out = Image.new("RGBA", (W, W), (0, 0, 0, 0))
draw = ImageDraw.Draw(out)


def lerp(c1, c2, t):
    return tuple(int(c1[i] + (c2[i] - c1[i]) * t) for i in range(4))


top = (58, 124, 255, 255)      # #3A7CFF
bottom = (0, 74, 208, 255)     # #004AD0

R = int(22 * SS)
# 渐变 + 圆角
for y in range(W):
    t = y / (W - 1)
    color = lerp(top, bottom, t)
    for x in range(W):
        inside = True
        if x < R and y < R:
            inside = (x - R) ** 2 + (y - R) ** 2 <= R ** 2
        elif x >= W - R and y < R:
            inside = (x - (W - R)) ** 2 + (y - R) ** 2 <= R ** 2
        elif x < R and y >= W - R:
            inside = (x - R) ** 2 + (y - (W - R)) ** 2 <= R ** 2
        elif x >= W - R and y >= W - R:
            inside = (x - (W - R)) ** 2 + (y - (W - R)) ** 2 <= R ** 2
        if inside:
            out.putpixel((x, y), color)

cx = cy = W // 2
ring = int(34 * SS)
lw = int(7 * SS)

WHITE = (255, 255, 255, 245)
# 上弧（顺时针箭头）
draw.arc(
    (cx - ring, cy - ring, cx + ring, cy + ring),
    start=200, end=340,
    fill=WHITE, width=lw,
)
# 下弧（逆时针箭头）
draw.arc(
    (cx - ring, cy - ring, cx + ring, cy + ring),
    start=20, end=160,
    fill=(255, 255, 255, 150), width=lw,
)

# 右上箭头头部
import math
ax = cx + ring * math.cos(math.radians(-20))
ay = cy + ring * math.sin(math.radians(-20))
ah = int(13 * SS)
ang = math.radians(-20 + 90)
p1 = (ax + ah * math.cos(ang + math.radians(150)), ay + ah * math.sin(ang + math.radians(150)))
p2 = (ax + ah * math.cos(ang - math.radians(150)), ay + ah * math.sin(ang - math.radians(150)))
draw.polygon([(ax + int(6 * SS) * math.cos(ang), ay + int(6 * SS) * math.sin(ang)), p1, p2], fill=WHITE)

# 左下箭头头部（下半弧，半透明）
ax2 = cx + ring * math.cos(math.radians(160))
ay2 = cy + ring * math.sin(math.radians(160))
ang2 = math.radians(160 + 90)
p1b = (ax2 + ah * math.cos(ang2 + math.radians(150)), ay2 + ah * math.sin(ang2 + math.radians(150)))
p2b = (ax2 + ah * math.cos(ang2 - math.radians(150)), ay2 + ah * math.sin(ang2 - math.radians(150)))
draw.polygon([(ax2 + int(6 * SS) * math.cos(ang2), ay2 + int(6 * SS) * math.sin(ang2)), p1b, p2b],
             fill=(255, 255, 255, 150))

# 中心实心节点
dr = int(11 * SS)
draw.ellipse((cx - dr, cy - dr, cx + dr, cy + dr), fill=WHITE)
# 中心镂空小圆
dr2 = int(4 * SS)
draw.ellipse((cx - dr2, cy - dr2, cx + dr2, cy + dr2), fill=(58, 124, 255, 255))

# 消息气泡（右下，代表事件推送）
bx, by = int(84 * SS), int(86 * SS)
bw, bh = int(24 * SS), int(15 * SS)
draw.rounded_rectangle((bx, by, bx + bw, by + bh), radius=int(5 * SS), fill=(255, 255, 255, 235))
draw.polygon([(bx + 5 * SS, by + bh), (bx + 9 * SS, by + bh + 5 * SS), (bx + 12 * SS, by + bh)],
             fill=(255, 255, 255, 235))

# 下采样抗锯齿
final = out.resize((SIZE, SIZE), Image.LANCZOS)
# 输出到仓库根目录（tools/ 的上一级）
repo_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
out_path = os.path.join(repo_root, "package.png")
final.save(out_path, "PNG")
print(f"saved {out_path} ({SIZE}x{SIZE})")
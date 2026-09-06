import re, html

path = r'E:/Netor.me/Madorin/Netor.Madorin.Ai.Runtime/docs/samples/一起来认识一个超快的Agent：Pi.html'
out = r'E:/Netor.me/Madorin/Netor.Madorin.Ai.Runtime/docs/samples/_pi_article.txt'

with open(path, encoding='utf-8', errors='ignore') as f:
    s = f.read()

m = re.search(r'<div class="rich_media_content[^>]*>(.*?)</div>\s*</div>', s, re.S)
t = m.group(1) if m else s
t = re.sub(r'<script.*?</script>', '', t, flags=re.S)
t = re.sub(r'<style.*?</style>', '', t, flags=re.S)
t = re.sub(r'<[^>]+>', '\n', t)
t = html.unescape(t)
t = re.sub(r'\n{3,}', '\n\n', t)

with open(out, 'w', encoding='utf-8') as f:
    f.write(t)

print('chars:', len(t))
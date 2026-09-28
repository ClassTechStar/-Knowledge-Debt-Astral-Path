# -*- coding: utf-8 -*-
import re, sys
xml = open(sys.argv[1], encoding='utf-8').read()
texts = re.findall(r'text="([^"]+)"', xml)
texts = [t for t in texts if t.strip()]
print('text nodes', len(texts))
for t in texts[:50]:
    print(' ', t[:100])

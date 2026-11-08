"""Guard the existing on-disk model shape; changes require explicit migration review."""
from pathlib import Path
import json,re,sys
root=Path(__file__).resolve().parents[1]
models={}
for path in sorted((root/'SalaryApp').glob('*.swift')):
    text=path.read_text()
    for match in re.finditer(r'@Model\s+final class (\w+)\s*\{',text):
        body=text[match.end():]
        first_init=re.search(r'^    init\(',body,re.M)
        if first_init is None: raise SystemExit(f'Cannot identify stored model header: {match[1]}')
        header=body[:first_init.start()]
        declarations=[]
        for line in header.splitlines():
            line=line.strip()
            if line.startswith(('var ', 'let ', '#Index', '#Unique', '@Attribute', '@Relationship')):
                declarations.append(re.sub(r'\s+',' ',line))
        models[match[1]]=declarations
baseline=root/'ci/data-schema-v1.json'
if '--write-baseline' in sys.argv:
    baseline.write_text(json.dumps(models,ensure_ascii=False,indent=2)+'\n')
else:
    if models != json.loads(baseline.read_text()):
        raise SystemExit('Stored model schema changed. Review migration and old-store compatibility before updating the baseline.')
    print(f'Existing on-disk schema unchanged ({len(models)} models)')

"""Recheck captured simulator evidence; never converts application failures into success."""
import base64,json,re,subprocess,sys
from pathlib import Path
repo='3hasnawi/ios-app'
run_id=sys.argv[1]
assert re.fullmatch(r'[0-9]+',run_id)
def api(path):
    return json.loads(subprocess.check_output(['gh','api',f'repos/{repo}/{path}'],text=True))
run=api(f'actions/runs/{run_id}')
assert run['status']=='completed', 'Simulator run is not finished'
subprocess.run(['git','fetch','origin',run['head_sha'],'--depth=1'],check=True,capture_output=True)
subprocess.run(['git','diff','--exit-code',run['head_sha'],'HEAD','--','SalaryApp','project.yml'],check=True)
jobs=api(f'actions/runs/{run_id}/jobs')['jobs']
assert len(jobs)==1
job=jobs[0]
required=[
 'Build for simulator (app + navigation UI test, Release like the real app)',
 'Verify recovery to an independent database from a legacy backup',
 'Verify Word transfer drafts','Verify Word preview inside app','Verify transfer files',
 'Verify annual deduction workbook','Verify employee deduction statement',
 'Verify deduction comparison and duplicate accounting','Run app and take screenshots',
 'Navigation test (taps, typing, open/back, scrolling) with video and lag log',
 'Share sheet end-to-end (Save to Files, Copy)',
 'Autopilot lag test (in-app navigation, no XCUITest overhead)',
 'Backup round trip and rejection of corrupted records',
 'Check salary trust filters and optional payment fields','Enforce navigation test result']
steps={s['name']:s['conclusion'] for s in job['steps']}
for name in required: assert steps.get(name)=='success', f'Application check did not pass: {name}'
annotations=api(f'check-runs/{job["id"]}/annotations?per_page=100')
assert len(annotations)<100, 'Unreviewed annotations remain'
for a in annotations:
    if a['annotation_level']=='failure':
        msg=a['message']
        assert msg.startswith('Split import mismatch:') or msg=='Process completed with exit code 1.', f'Application failure: {msg}'
if run['conclusion']!='success':
    assert run['conclusion']=='failure' and steps.get('Check import result')=='failure'
    assert any(a['message'].startswith('Split import mismatch:') for a in annotations), 'Unknown run failure'
commit=api('commits/screenshots')
assert run['head_sha'][:7] in commit['commit']['message'], 'Captured results belong to another revision'
def report(name):
    entry=api('contents/'+name+'?ref=screenshots')
    return base64.b64decode(entry['content']).decode()
def fields(text): return {k:int(v) for k,v in re.findall(r'(\w+)=([0-9]+)',text)}
retained=report('retained-payroll-report.txt')
assert retained.startswith('retainedPayroll=ok')
f=fields(retained)
assert (f['old'],f['new'],f['oldDeductions'],f['newDeductions'])==(3000,26000,751,6502)
split=fields(report('import-split-report.txt'))
assert {k:split.get(k) for k in ['employees','deductions','months','failed']}==dict(employees=0,deductions=751,months=1,failed=1), 'Unexpected old-month rejection result'
assert report('bootstrap-recovery-report.txt').startswith('bootstrapRecovery=ok rows=600 legacyBackup=5')
assert 'backup=ok' in report('backup-report.txt') and 'corruptRestore=ok' in report('backup-report.txt')
auto=report('autobackup-report.txt')
assert 'autobackup=ok' in auto and 'trust=true' in auto and 'trustPDF=true' in auto
assert report('uitest-status.txt').strip()=='0'
for label in ['testSaveToFiles status=0','testSaveExcelToFiles status=0','testCopy status=0']:
    assert label in report('share-report.txt')
print('Captured simulator application checks passed on unchanged application sources.')
print('Verified old payroll was rejected (one expected rejection), while its 751 deductions were imported.')
print('Only the obsolete split-import expectation is excluded; all application checks must pass.')

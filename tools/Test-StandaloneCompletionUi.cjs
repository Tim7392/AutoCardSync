'use strict';
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const test = require('node:test');

// Execute the actual UI predicates without starting WebView or the desktop host.
const source = fs.readFileSync(path.join(__dirname, '../src/AutoCardSync.Standalone/WebUI/scripts/app.js'), 'utf8');
function definition(name) {
  const start = source.indexOf(`  function ${name}(`);
  assert.notEqual(start, -1, `Missing UI predicate ${name}`);
  const end = source.indexOf('\n  function ', start + 1);
  return source.slice(start, end < 0 ? undefined : end);
}
const labelsStart = source.indexOf('  const TARGET_STATE_LABELS =');
const labelsEnd = source.indexOf('\n  });', labelsStart) + '\n  });'.length;
assert.ok(labelsStart >= 0 && labelsEnd > labelsStart);
const context = vm.createContext({});
vm.runInContext(source.slice(labelsStart, labelsEnd) + '\n' +
  ['isPlainObject', 'normalizeTargetMode', 'validateTarget', 'safetyComplete', 'completionEvidence', 'mediaIsComplete']
    .map(definition).join('\n'), context);

function completedTask(mode = 'local-only') {
  const target = kind => ({ kind, state: 'complete', copyPercent: 100, verificationPercent: 100,
    bytesPerSecond: 0, verificationBytesPerSecond: 0, copyEtaSeconds: 0, verificationEtaSeconds: 0,
    verificationBytes: 7, verificationTotalBytes: 7 });
  return { totalFiles: 1, completedFiles: 1, targetMode: mode, taskVerified: true,
    verificationScope: 'task', safeToClear: false, safeToRemoveCard: false,
    currentInventoryFiles: 2, verifiedCurrentFiles: 1,
    taskSafety: { manifestFrozen: true, sourceReadOnly: true, allIncludedFilesAccountedFor: true,
      finalObjectsSafelyAvailable: true, sourceIdentityUnchanged: true, targetIdentitiesUnchanged: true,
      localCompletionReceiptPersisted: true, safeToRemoveCard: true,
      localTargetFullRereadSha256: mode === 'nas-only' ? 'NOT_REQUIRED' : 'PASS',
      nasTargetFullRereadSha256: mode === 'local-only' ? 'NOT_REQUIRED' : 'PASS',
      failedIncludedFiles: 0, pendingIncludedFiles: 0 },
    targets: mode === 'local-and-nas' ? [target('local'), target('mappedNas')]
      : [target(mode === 'nas-only' ? 'mappedNas' : 'local')] };
}

for (const mode of ['local-only', 'nas-only', 'local-and-nas']) {
  test(`${mode}: a completed incremental task does not clear historical material`, () => {
    const result = context.completionEvidence(completedTask(mode));
    assert.equal(result.taskComplete, true);
    assert.equal(result.wholeInventory, false);
  });
}
test('current inventory requires matching verified coverage and an explicit current conclusion', () => {
  const value = completedTask();
  Object.assign(value, { verificationScope: 'current-inventory', verifiedCurrentFiles: 2,
    safeToClear: true, safeToRemoveCard: true });
  assert.equal(context.completionEvidence(value).wholeInventory, true);
  value.verifiedCurrentFiles = 1;
  assert.equal(context.completionEvidence(value).wholeInventory, false);
});
test('metadata, history, progress and missing target proof cannot become current completion', () => {
  for (const value of [null, { view: 'baseline', baselinePersisted: true },
    { state: 'completed', safeToRemoveCard: true }, { overallPercent: 100 }]) {
    assert.equal(context.completionEvidence(value).wholeInventory, false);
    assert.equal(context.completionEvidence(value).taskComplete, false);
  }
  const value = completedTask();
  value.targets = [];
  assert.equal(context.completionEvidence(value).taskComplete, false);
});
test('card center excludes task-only and baseline observations from its verified group', () => {
  for (const conclusion of ['no_backup_conclusion', 'task_verified']) {
    assert.equal(context.mediaIsComplete({ workState: 'completed', safetyConclusion: conclusion }), false);
  }
  assert.equal(context.mediaIsComplete({ safetyConclusion: 'approved_material_verified' }), true);
});

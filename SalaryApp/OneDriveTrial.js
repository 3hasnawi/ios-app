"use strict";
const trialPrefix = "payroll-trial-v1-";
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
const limit = 250;
const members = ["منتسب تجريبي أ", "منتسب تجريبي ب"];
function message(ar,en) { return typeof document !== "undefined" && document.documentElement.lang === "en" ? en : ar; }
function check(ok, detail = message("تعذّر التحقق من بيانات التجربة.","Test data could not be verified.")) {
    if (!ok) throw new Error(detail);
}
function parseEdit(raw) {
    check(raw && uuidPattern.test(raw.id) && [0,1].includes(raw.member));
    check([10000,20000,30000].includes(raw.amount));
    check(Number.isSafeInteger(raw.time) && raw.time > 0 && raw.time <= 8640000000000000);
    check(Array.isArray(raw.parents) && raw.parents.length <= limit);
    check(raw.parents.every(id => uuidPattern.test(id) && id !== raw.id));
    const parents = [...new Set(raw.parents)].sort();
    check(parents.length === raw.parents.length);
    return {id:raw.id, member:raw.member, amount:raw.amount, time:raw.time, parents};
}
function combine(...groups) {
    const map = new Map();
    for (const raw of groups.flat()) {
        const edit = parseEdit(raw);
        const previous = map.get(edit.id);
        check(!previous || JSON.stringify(previous) === JSON.stringify(edit), message("تعارض في محتوى ملف التجربة.","Conflicting contents for one test file."));
        map.set(edit.id,edit);
    }
    check(map.size <= limit, message("بلغت التجربة حدها البالغ ٢٥٠ تعديلاً.","The test is limited to 250 edits."));
    return [...map.values()];
}
function validateHistory(edits) {
    const byId = new Map(edits.map(e => [e.id,e]));
    const completed = new Set(), visiting = new Set();
    function visit(edit) {
        if (completed.has(edit.id)) return;
        check(!visiting.has(edit.id));
        visiting.add(edit.id);
        for (const id of edit.parents) {
            const parent = byId.get(id);
            check(parent && parent.member === edit.member, message("سجل التجربة غير مكتمل. أعد المزامنة.","The edit history is incomplete. Retry sync."));
            visit(parent);
        }
        visiting.delete(edit.id);
        completed.add(edit.id);
    }
    edits.forEach(visit);
}
function latest(a,b) {
    return b.time - a.time || (a.id < b.id ? 1 : a.id === b.id ? 0 : -1);
}
function heads(edits,member) {
    const rows = edits.filter(e => e.member === member);
    const superseded = new Set(rows.flatMap(e => e.parents));
    return rows.filter(e => !superseded.has(e.id)).sort(latest);
}
function decodeState(text) {
    if (text === "") return {version:1,folder:"",edits:[],pending:[]};
    const s = JSON.parse(text);
    check(s.version === 1 && typeof s.folder === "string" && s.folder.length > 0);
    check(Array.isArray(s.edits) && Array.isArray(s.pending));
    const edits = combine(s.edits);
    validateHistory(edits);
    check(s.pending.every(id => edits.some(e => e.id === id)));
    return {version:1,folder:s.folder,edits,pending:[...new Set(s.pending)]};
}
function addEdit(state, member, amount, id, time) {
    check(state.folder, message("اربط الجهاز بحساب التجربة أولاً.","Link the device to the test account first."));
    const edit = parseEdit({id,member,amount,time,parents:heads(state.edits,member).map(e => e.id)});
    return {...state, edits:combine(state.edits,[edit]),pending:[...state.pending,id]};
}
async function synchronize(state, api) {
    const folder = await api("folder");
    check(folder && (!state.folder || state.folder === folder), message("هذا حساب OneDrive مختلف عن الحساب المرتبط بالتجربة على هذا الجهاز.","The selected OneDrive differs from the account linked to this test."));
    const getIds = async () => {
        const names = JSON.parse(await api("list"));
        check(Array.isArray(names));
        const ids = new Set();
        for (const name of names) {
            if (!name.startsWith(trialPrefix)) continue;
            const id = name.slice(trialPrefix.length,-5);
            check(uuidPattern.test(id) && name === trialPrefix + id + ".json");
            ids.add(id);
        }
        check(ids.size <= limit);
        return ids;
    };
    const known = new Map(state.edits.map(e => [e.id,e]));
    const pending = new Set(state.pending);
    const remote = await getIds();
    const read = async id => {
        const edit = parseEdit(JSON.parse(await api("read",id)));
        check(edit.id === id);
        return edit;
    };
    for (const e of state.edits)
        check(remote.has(e.id) || pending.has(e.id), message("هناك ملف تجريبي مؤكد مفقود من OneDrive. راجع سجل الملفات.","OneDrive is missing a previously confirmed edit. Check the file history."));
    const fresh = [];
    for (const id of remote) fresh.push(await read(id));
    const merged = combine(state.edits,fresh);
    validateHistory(merged);
    for (const id of pending) {
        if (remote.has(id)) continue;
        await api("write",id,JSON.stringify(known.get(id)));
        combine([known.get(id)],[await read(id)]);
    }
    // Include edits published by other devices while this device was writing.
    const final = await getIds();
    const mergedIds = new Set(merged.map(e=>e.id));
    check(merged.every(e => final.has(e.id)), message("بانتظار تأكيد حفظ الملفات. أعد المزامنة.","Waiting for read-back confirmation. Retry sync."));
    const tail = [];
    for (const id of final) if (!mergedIds.has(id)) tail.push(await read(id));
    const edits = combine(merged,tail);
    validateHistory(edits);
    return {version:1,folder,edits,pending:[]};
}
if (typeof module !== "undefined" && module.exports)
    module.exports = {combine,validateHistory,decodeState,addEdit,synchronize,heads,trialPrefix};

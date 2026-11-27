const {test}=require("node:test");
const assert=require("node:assert/strict");
const {randomUUID}=require("node:crypto");
const {addEdit,decodeState,synchronize,heads,trialPrefix,combine,validateHistory}=require("../SalaryApp/OneDriveTrial.js");

function cloud() {
    const files=new Map();
    async function api(action,id,value) {
        if (action==="folder") return "drive-A/folder-A";
        if (action==="list") return JSON.stringify(["unrelated.txt",...Array.from(files.keys(),k=>trialPrefix+k+".json")]);
        if (action==="read") { assert.ok(files.has(id)); return files.get(id); }
        assert.equal(action,"write");
        // Matches Graph's conflictBehavior=fail + a subsequent content verification.
        if (!files.has(id)) files.set(id,value);
        return "";
    }
    return {files,api};
}
const edit=(s,member,amount,time=Date.now())=>addEdit(s,member,amount,randomUUID(),time);
const initial=()=>decodeState("");
const reopen=s=>decodeState(JSON.stringify(s));
const totals=s=>[0,1].reduce((total,i)=>total+(heads(s.edits,i)[0]?.amount||0),0);

test("three devices, disconnected edits to different records, restart and convergence",async()=>{
    const {api,files}=cloud();
    let [phone,office,home]=await Promise.all([1,2,3].map(()=>synchronize(initial(),api)));
    phone=edit(phone,0,10000);
    office=edit(office,1,20000);
    await Promise.all([synchronize(reopen(phone),api),synchronize(reopen(office),api)]);
    for (const device of [phone,office,home]) {
        const synced=await synchronize(device,api);
        assert.equal(totals(synced),30000);
        assert.equal(synced.edits.length,2);
        assert.equal(synced.pending.length,0);
    }
    assert.equal(files.size,2);
});
test("conflicting offline amounts, equal timestamps, resolution despite clock skew",async()=>{
    const {api}=cloud();
    const base=await synchronize(edit(await synchronize(initial(),api),0,10000),api);
    const a=edit(base,0,20000,50), b=edit(base,0,30000,50);
    await Promise.all([synchronize(a,api),synchronize(b,api)]);
    const both=await synchronize(base,api);
    assert.deepEqual(heads(both.edits,0).map(e=>e.amount).sort(),[20000,30000]);
    assert.equal(totals(both),totals({...both,edits:both.edits.slice().reverse()}));
    const fixed=await synchronize(edit(both,0,10000,10),api);
    assert.equal(heads(fixed.edits,0).length,1);
    assert.equal(totals(fixed),10000);
    assert.equal(fixed.edits.length,4);
});
test("lost response after upload and partial batch restart never double-count",async()=>{
    const {api,files}=cloud();
    let s=edit(edit(await synchronize(initial(),api),0,10000),1,30000);
    await assert.rejects(synchronize(s,async(...args)=>{
        const result=await api(...args);
        if(args[0]==="write")throw Error("network lost after commit");
        return result;
    }));
    assert.equal(s.pending.length,2);assert.equal(files.size,1);
    const replay=await synchronize(reopen(s),api);
    assert.equal(totals(replay),40000);assert.equal(files.size,2);
});
test("foreign account, missing previously confirmed edit and corrupted readback block acknowledgement",async()=>{
    const {api,files}=cloud();
    const s=edit(await synchronize(initial(),api),0,10000);
    await assert.rejects(synchronize(s,(op,...a)=>op==="folder"?"other":api(op,...a)));
    assert.equal(files.size,0);
    await assert.rejects(synchronize(s,async(op,...a)=>{
        const result=await api(op,...a);
        return op==="read"?JSON.stringify({...JSON.parse(result),amount:30000}):result;
    }));
    const verified=await synchronize(s,api);
    files.clear();await assert.rejects(synchronize(verified,api));
});
test("malformed history, reused IDs and cycles are rejected",()=>{
    const s={...initial(),folder:"fake"};
    const e=edit(s,0,10000).edits[0], f=edit(s,1,20000).edits[0];
    assert.throws(()=>combine([e],[{...e,amount:20000}]));
    assert.throws(()=>validateHistory([e,{...f,parents:[e.id]}]));
    assert.throws(()=>validateHistory([{...e,parents:[f.id]},{...f,member:0,parents:[e.id]}]));
    assert.throws(()=>decodeState(JSON.stringify({...s,edits:[e],pending:["invalid"]})));
});

const {test}=require("node:test");
const assert=require("node:assert/strict");
const {randomUUID}=require("node:crypto");
const {addEdit,decodeState,synchronize,heads,trialPrefix,combine,validateHistory,makeRefund,refundRecords}=require("../SalaryApp/OneDriveTrial.js");

function cloud() {
    const files=new Map(), refunds=new Map();
    async function api(action,id,value) {
        if (action==="folder") return "drive-A/folder-A";
        if (action==="list") return JSON.stringify(["unrelated.txt",...Array.from(files.keys(),k=>trialPrefix+k+".json"),...Array.from(refunds.keys(),k=>refundRecords.prefix+k+".json")]);
        if (action==="refundWrite") { if (!refunds.has(id)) refunds.set(id,value); return ""; }
        if (action==="refundRead") { assert.ok(refunds.has(id)); return refunds.get(id); }
        if (action==="read") { assert.ok(files.has(id)); return files.get(id); }
        assert.equal(action,"write");
        // Matches Graph's conflictBehavior=fail + a subsequent content verification.
        if (!files.has(id)) files.set(id,value);
        return "";
    }
    return {files,refunds,api};
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

test("visible trial screens exchange edits automatically and recover after a failed poll", async () => {
    const {readFileSync} = require("node:fs");
    const vm = require("node:vm");
    const {api} = cloud();
    const page = readFileSync(require.resolve("../SalaryApp/OneDriveTrial.html"), "utf8");
    const engine = readFileSync(require.resolve("../SalaryApp/OneDriveTrial.js"), "utf8");
    const script = [...page.matchAll(/<script>([\s\S]*?)<\/script>/g)].map(match => match[1]).join("\n")
        .replace("/* TRIAL_ENGINE */", () => engine);

    async function device() {
        const events = {}, timers = new Map(), fields = new Map();
        let saved="", opened, local = "", fail = false, requests = 0, timerID = 0;
        const field = id => {
            if (!fields.has(id)) fields.set(id, {options:[{value:"0"},{value:"1"}],value:"0",children:[],append(child){this.children.push(child);},replaceChildren(){this.children=[];}});
            return fields.get(id);
        };
        const listen = (name,callback) => { events[name]=callback; };
        const document = {documentElement:{lang:"ar",dataset:{numbers:"latn"}},hidden:false,
            getElementById:field, createElement:()=>({}), addEventListener:listen};
        const navigator = {onLine:true};
        const window = {addEventListener:listen,webkit:{messageHandlers:{trial:{async postMessage({action,id,value}) {
            if (action==="openRefund") { opened=JSON.parse(value); return ""; }
            if (action==="refundLoad") return saved;
            if (action==="refundStore") { saved=value; return ""; }
            if (action==="store") { local=value; return ""; }
            if (action==="load") return local;
            if (action==="device" || action==="login") return "";
            requests++;
            if (fail) throw Error("offline");
            return api(action,id,value);
        }}}}};
        const context = {window,document,navigator,crypto:require("node:crypto").webcrypto,
            setTimeout(fn,delay){timers.set(++timerID,{fn,delay});return timerID;},
            clearTimeout(id){timers.delete(id);}};
        vm.runInNewContext(script,context);
        await new Promise(resolve=>setImmediate(resolve));
        assert.equal(timers.size,0);
        await field("login").onclick();
        return {field,document,navigator,events,timers,opened:()=>opened,refunds:()=>decodeState(saved,refundRecords),state:()=>decodeState(local),
            fail(value){fail=value;}, count:()=>requests,
            async tick(delay) {
                assert.equal(timers.size,1);
                const [id,timer]=[...timers][0];
                assert.equal(timer.delay,delay);
                timers.delete(id);
                await timer.fn();
            }};
    }

    const phone=await device(), desktop=await device();
    phone.field("amount").value="30000";
    await phone.field("save").onclick();
    assert.equal(totals(desktop.state()),0);
    await desktop.tick(15000);
    assert.equal(totals(desktop.state()),30000);
    const requests=desktop.count();
    await desktop.tick(15000);
    assert.equal(desktop.count()-requests,2); // مجلد الحساب وقائمة الملفات فقط.
    desktop.fail(true);
    await desktop.tick(15000);
    assert.equal(desktop.field("status").textContent,"offline");
    desktop.fail(false);
    await desktop.tick(30000);
    assert.equal(desktop.timers.size,1);
    desktop.document.hidden=true;
    desktop.events.visibilitychange();
    assert.equal(desktop.timers.size,0);
    desktop.navigator.onLine=false;
    desktop.document.hidden=false;
    desktop.events.visibilitychange();
    assert.equal(desktop.timers.size,0);
    phone.field("amount").value="20000";
    await phone.field("save").onclick();
    desktop.navigator.onLine=true;
    desktop.events.online();
    await new Promise(resolve=>setImmediate(resolve));
    assert.equal(totals(desktop.state()),20000);
    assert.equal(desktop.timers.size,1);
    assert.equal(phone.field("saveRefund").disabled,true);
    phone.field("member").value="1";
    phone.field("amount").value="30000";
    await phone.field("save").onclick();
    await phone.field("saveRefund").onclick();
    assert.equal(phone.refunds().pending.length,0);
    await desktop.tick(15000);
    await desktop.field("refundHistory").children[0].onclick();
    assert.deepEqual(desktop.opened(),phone.refunds().edits[0].snapshot);
    desktop.navigator.onLine=false;
    desktop.events.offline();
    await desktop.field("saveRefund").onclick();
    assert.equal(desktop.refunds().pending.length,1);
    desktop.navigator.onLine=true;
    desktop.events.online();
    await new Promise(resolve=>setImmediate(resolve));
    await phone.tick(15000);
    assert.deepEqual(phone.refunds().edits,desktop.refunds().edits);
    assert.equal(phone.refunds().edits.length,2);
    assert.ok(desktop.field("refundStatus").textContent.includes("آخر مزامنة"));
});

test("saved refunds verify content before acknowledgement, survive restart and reject another drive",async()=>{
    const {api,refunds}=cloud();
    const options={records:refundRecords};
    const base=await synchronize(initial(),api,options);
    const batch=makeRefund(randomUUID(),"2026-11-27",[10000,30000]);
    const queued={...base,edits:[batch],pending:[batch.id]};
    await assert.rejects(synchronize(queued,(op,...args)=>op==="folder"?"other":api(op,...args),options));
    assert.equal(refunds.size,0);
    await assert.rejects(synchronize(queued,async(op,...args)=>{
        const result=await api(op,...args);
        if(op==="refundWrite")throw Error("lost response");
        return result;
    },options));
    const restarted=decodeState(JSON.stringify(queued),refundRecords);
    const received=await synchronize(restarted,api,options);
    assert.deepEqual(received.pending,[]);
    assert.equal(refunds.size,1);
    const damaged=structuredClone(batch);
    damaged.snapshot.rows[0].candidate.person.iban=batch.snapshot.account;
    refunds.set(batch.id,JSON.stringify(damaged));
    await assert.rejects(synchronize(received,api,options));
    refunds.clear();
    await assert.rejects(synchronize(received,api,options));
});

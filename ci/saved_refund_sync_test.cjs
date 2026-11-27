const {test} = require("node:test");
const assert = require("node:assert/strict");
const {createHash,randomUUID} = require("node:crypto");
const {syncSavedRefund,savedPrefix} = require("../SalaryApp/SavedRefundSync.js");
const {makeRefund} = require("../SalaryApp/OneDriveTrial.js");

const batch = randomUUID();
const raw = (a=10000,b=20000,id=batch) => JSON.stringify(makeRefund(id,"2026-11-27",[a,b]).snapshot);
const digest = s => createHash("sha256").update(s).digest("hex");
function device(cloud, initial="") {
    const d = {local:initial,state:"",folder:"drive/folder",writes:0,online:true};
    async function api(action,id,value) {
        if(!d.online && ["folder","read","list","write"].includes(action)) throw Error("offline");
        const path=savedPrefix+id+".json";
        switch(action){
            case "hash": return digest(value);
            case "folder": return d.folder;
            case "local": return d.local;
            case "load": return d.state;
            case "store": d.state=value;return "";
            case "list": return JSON.stringify([...cloud.keys()]);
            case "claim": if(!cloud.has("payroll-refund-link-v1.json"))cloud.set("payroll-refund-link-v1.json",id);return "";
            case "claimRead": return cloud.get("payroll-refund-link-v1.json");
            case "read": return cloud.get(path);
            case "write":
                if(!cloud.has(path))cloud.set(path,value);
                if(d.interrupt==="write"){d.interrupt="";throw Error("offline");}
                return "";
            case "validate": assert.equal(JSON.parse(value).batchID.toLowerCase(),id);return "";
            case "apply": {
                const {expected,snapshot}=JSON.parse(value);
                if(d.beforeApply){ d.local=d.beforeApply;d.beforeApply=null; }
                assert.equal(expected,d.local?digest(d.local):"");
                d.local=snapshot;d.writes++;
                if(d.interrupt==="apply"){d.interrupt="";throw Error("closed");}
                return "";
            }
        }
        throw Error(action);
    }
    d.sync=(id="",choice=null)=>syncSavedRefund(api,id,choice);
    return d;
}

test("one opt-in batch replicates both ways, including letters, source and payment rows",async()=>{
    const cloud=new Map([["payroll-trial-v1-unrelated.json","ignored"]]);
    const iphone=device(cloud,raw()), work=device(cloud), home=device(cloud);
    await work.sync();
    assert.equal(work.local,"");
    await iphone.sync(batch);
    await work.sync();await home.sync();
    assert.equal(work.local,iphone.local);assert.equal(home.local,iphone.local);
    work.online=false;work.local=raw(30000);
    await assert.rejects(work.sync(),/offline/);
    work.online=true;await work.sync();await iphone.sync(batch);await home.sync();
    assert.equal(iphone.local,raw(30000));assert.equal(home.local,raw(30000));
    const size=cloud.size; await work.sync();assert.equal(cloud.size,size);
    await assert.rejects(work.sync(randomUUID()),/دفعة واحدة/);
    work.folder="other/account";await assert.rejects(work.sync(),/حساب OneDrive/);
});

test("competing offline edits survive; choosing a current head resolves all devices",async()=>{
    const cloud=new Map(), a=device(cloud,raw()),b=device(cloud);
    await a.sync(batch);await b.sync();
    a.local=raw(30000,10000);b.local=raw(20000,30000);
    await a.sync();const conflict=await b.sync();const again=await a.sync();
    assert.deepEqual(conflict.versions.map(v=>v.id),again.versions.map(v=>v.id));
    assert.deepEqual(new Set(conflict.versions.map(v=>v.snapshot)),new Set([a.local,b.local]));
    assert.equal((await b.sync("",{key:"stale",id:conflict.versions[0].id})).versions.length,2);
    const choice=conflict.versions.find(v=>v.snapshot===a.local);
    await b.sync("",{key:conflict.key,id:choice.id});await a.sync();
    assert.equal(b.local,a.local);
    assert.equal((await a.sync()).versions,undefined);
});

test("retries after upload or local commit do not duplicate the batch",async()=>{
    const c=new Map(),a=device(c,raw()),b=device(c);
    a.interrupt="write";await assert.rejects(a.sync(batch),/offline/);
    assert.equal(JSON.parse(a.state).folder,a.folder);
    const size=c.size;await a.sync(batch);assert.equal(c.size,size);
    b.interrupt="apply";await assert.rejects(b.sync(),/closed/);
    await b.sync();assert.equal(b.writes,1);assert.equal(b.local,a.local);
});

test("a receiver with divergent local data must opt in, and a stale apply cannot replace a new edit",async()=>{
    const c=new Map(),a=device(c,raw()),b=device(c,raw(30000));
    await a.sync(batch);await assert.rejects(b.sync(),/مختلفة/);
    assert.equal(b.local,raw(30000));
    assert.equal((await b.sync(batch)).versions.length,2);
    const c2=new Map(),sender=device(c2,raw()),receiver=device(c2);
    await sender.sync(batch);await receiver.sync();
    sender.local=raw(30000);await sender.sync();
    receiver.beforeApply=raw(20000,10000);
    await assert.rejects(receiver.sync());assert.equal(receiver.local,raw(20000,10000));
    assert.equal((await receiver.sync()).versions.length,2);
});

test("damaged or missing revisions stop sync without replacing the archive",async()=>{
    const c=new Map(),a=device(c,raw()),b=device(c);await a.sync(batch);
    const version=[...c.keys()].find(k=>k.startsWith(savedPrefix));
    c.set(version,c.get(version)+" "); await assert.rejects(b.sync(),/بصمة/);assert.equal(b.local,"");
    c.delete(version);await assert.rejects(a.sync(),/مفقودة/);
});

test("two concurrent first senders can only claim one batch",async()=>{
    const c=new Map(),id=randomUUID(),a=device(c,raw()),b=device(c,raw(10000,10000,id));
    const results=await Promise.allSettled([a.sync(batch),b.sync(id)]);
    assert.equal(results.filter(r=>r.status==="fulfilled").length,1);
    assert.equal([...c.keys()].filter(k=>k.startsWith(savedPrefix)).length,1);
});

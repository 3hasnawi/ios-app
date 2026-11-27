"use strict";

// كل مراجعة ملف مستقل؛ بصمته تشمل الدفعة والمراجعات التي تحل محلها.
const savedPrefix = "payroll-refund-saved-v1-";
const savedName = /^payroll-refund-saved-v1-([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\.([0-9a-f]{64})\.json$/;
const digestPattern = /^[0-9a-f]{64}$/;
const english = typeof document !== "undefined" && document.documentElement.lang === "en";
const savedText = (ar,en) => english ? en : ar;
const locale = typeof document === "undefined" ? "ar-IQ" :
    (english ? "en-US" : "ar-IQ") + "-u-nu-" + document.documentElement.dataset.numbers;
function requireSync(ok, message) { if (!ok) throw new Error(message); }
function summary(raw) {
    const batch = JSON.parse(raw);
    const total = batch.rows.reduce((sum,row)=>sum+BigInt(String(row.amount).replace(/[٠-٩]/g,c=>String("٠١٢٣٤٥٦٧٨٩".indexOf(c))).replace(/[۰-۹]/g,c=>String("۰۱۲۳۴۵۶۷۸۹".indexOf(c))).replace(/[,٬]/g,"").trim()),0n);
    return [batch.source || savedText("دفعة إعادة","Refund batch"),batch.rows.length.toLocaleString(locale)+savedText(" منتسب"," employees"),total.toLocaleString(locale)+savedText(" د.ع"," IQD")].join(" · ");
}
async function syncSavedRefund(api, selected, choice=null) {
    const folder=await api("folder");
    requireSync(folder,savedText("تعذّر تحديد حساب OneDrive.","Could not identify the OneDrive account."));
    const stored=await api("load");
    const link=stored ? JSON.parse(stored) : {folder,batch:"",base:"",stamp:"",graph:{},last:0};
    requireSync(link.folder===folder,savedText("اختر حساب OneDrive المرتبط بهذه الدفعة.","Choose the OneDrive account linked to this batch."));
    const listed=JSON.parse(await api("list"));
    const claimName="payroll-refund-link-v1.json";
    const claimed=listed.includes(claimName) ? await api("claimRead") : "";
    requireSync(!claimed || /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/.test(claimed),savedText("معرّف الدفعة المشتركة غير صالح.","Invalid shared batch ID."));
    const names=listed.filter(name=>name.startsWith(savedPrefix));
    const matches=names.map(name=>{
        const parts=savedName.exec(name);
        requireSync(parts,savedText("اسم ملف مراجعة الدفعة غير صالح.","Invalid batch revision filename."));
        return parts;
    });
    const ids=new Set(matches.map(m=>m[1]));
    const batch=selected || link.batch || claimed || [...ids][0];
    if (!batch) return {message:savedText("بانتظار مشاركة الدفعة من الجهاز الأول.","Waiting for the first device to share the batch.")};
    requireSync((!link.batch || link.batch===batch) && (!claimed || claimed===batch) && [...ids].every(id=>id===batch),
        savedText("تخص هذه المرحلة دفعة واحدة. اختر الدفعة المرتبطة نفسها.","This pilot shares one batch. Select the linked batch."));
    const remote=new Set(matches.map(m=>m[2]));
    requireSync(Object.keys(link.graph).every(id=>remote.has(id)) && (!link.base || remote.has(link.base)),
        savedText("إحدى المراجعات السابقة مفقودة من OneDrive. توقفت المزامنة لحماية الدفعة.","A previous revision is missing from OneDrive. Sync stopped to protect the batch."));
    const payloads=new Map();
    const hash=value=>api("hash","",value);
    const path=id=>batch+"."+id;
    async function fetchVersion(id) {
        if (payloads.has(id)) return payloads.get(id);
        const body=await api("read",path(id));
        requireSync(await hash(body)===id,savedText("فشل التحقق من بصمة الدفعة.","The batch checksum did not match."));
        const version=JSON.parse(body);
        requireSync(version.schema===1 && Array.isArray(version.parents) &&
            new Set(version.parents).size===version.parents.length &&
            version.parents.every(parent=>digestPattern.test(parent) && remote.has(parent)) &&
            JSON.parse(version.snapshot).batchID.toLowerCase()===batch,savedText("مراجعة الدفعة غير مكتملة.","The batch revision is incomplete."));
        await api("validate",batch,version.snapshot);
        payloads.set(id,version);
        return version;
    }
    for (const id of remote) if (!Object.hasOwn(link.graph,id)) link.graph[id]=(await fetchVersion(id)).parents;
    function heads() {
        const parents=new Set(Object.values(link.graph).flat());
        return [...remote].filter(id=>!parents.has(id)).sort();
    }
    async function publish(snapshot,parents) {
        await api("validate",batch,snapshot);
        const body=JSON.stringify({schema:1,parents:[...parents].sort(),snapshot});
        const id=await hash(body);
        if (!remote.has(id)) {
            await api("write",path(id),body);
            remote.add(id);
            await fetchVersion(id);
            link.graph[id]=parents;
        }
        return id;
    }
    const local=await api("local",batch);
    const stamp=local ? await hash(local) : "";
    requireSync(local || !selected && !link.base,
        savedText("الدفعة المرتبطة غير موجودة محلياً. راجع الدفعات المحفوظة قبل المتابعة.","The linked batch is missing locally. Review your saved batches."));
    if (!claimed) {
        requireSync(!remote.size && !link.base,savedText("تعذّر التحقق من ارتباط الدفعة بمجلد OneDrive.","Could not verify the batch link in OneDrive."));
        if (!selected || !local) return {message:savedText("بانتظار مشاركة الدفعة من الجهاز الأول.","Waiting for the first device to share the batch.")};
        await api("validate",batch,local);
        await api("claim",batch);
        requireSync(await api("claimRead")===batch,savedText("اختار جهاز آخر دفعة مختلفة لهذا الحساب. افتح الدفعة المرتبطة.","Another device linked a different batch. Open that batch to continue."));
    }
    if (!stored) {
        link.batch=batch;
        await api("store","",JSON.stringify(link));
    }
    if (!remote.size && !local) return {message:savedText("بانتظار اكتمال إرسال الدفعة من الجهاز الأول.","Waiting for the first device to finish uploading.")};
    let tips=heads();
    requireSync(!remote.size || tips.length,savedText("تعذّر تحديد أحدث مراجعة للدفعة.","Could not identify the latest batch revision."));
    const one=tips.length===1 ? tips[0] : "";
    const alreadyMatches=one && (link.base===one && link.stamp===stamp || (await fetchVersion(one)).snapshot===local);
    if (local && !alreadyMatches && (!link.base || stamp!==link.stamp)) {
        requireSync(link.base || selected,savedText("توجد دفعة محلية مختلفة بالمعرّف نفسه. افتح «مزامنة هذه الدفعة» من سجلها لحفظ النسختين ومراجعتهما.","A different local batch has the same ID. Open Sync this batch from its entry to preserve and review both versions."));
        await publish(local,link.base?[link.base]:[]);
        tips=heads();
    }
    link.batch=batch;
    if (tips.length>1) {
        const key=[stamp,...tips].join(".");
        if (!choice || choice.key!==key || !tips.includes(choice.id)) {
            const versions=[];
            for (const id of tips) {
                const {snapshot}=await fetchVersion(id);
                versions.push({id,label:summary(snapshot),snapshot});
            }
            await api("store","",JSON.stringify(link));
            return {message:savedText("يوجد تعارض. راجع المنتسبين والمبالغ واعتمد إحدى النسخ.","Conflicting edits. Review both versions and choose one."),versions,key};
        }
        const {snapshot}=await fetchVersion(choice.id);
        tips=[await publish(snapshot,tips)];
    }
    requireSync(tips.length===1,savedText("لم تصل الدفعة إلى OneDrive بعد.","The batch has not reached OneDrive yet."));
    const tip=tips[0];
    let confirmed=stamp;
    if (tip!==link.base || stamp!==link.stamp) {
        const {snapshot}=await fetchVersion(tip);
        if (snapshot!==local) await api("apply",batch,JSON.stringify({expected:stamp,snapshot}));
        confirmed=await hash(snapshot);
    }
    const changed=tip!==link.base || confirmed!==link.stamp || !stored;
    if (changed) {
        link.stamp=confirmed;link.base=tip;link.last=Date.now();
        await api("store","",JSON.stringify(link));
    }
    return {message:savedText("تمت مزامنة الدفعة","Batch synced"),last:link.last};
}
if (typeof module!=="undefined") module.exports={syncSavedRefund,savedPrefix};

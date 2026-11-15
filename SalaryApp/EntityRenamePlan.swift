import Foundation

/// يغيّر هوية المصدر فقط؛ لا يغيّر أرقام الحسابات أو نصوص الكتب الصادرة.
nonisolated enum EntityRenamePlan {
    static let linksKey = "transferPayrollAccountLinks"
    static let accountsKey = "executionAccountsData"
    static let categoriesKey = "transferEntityCategories"
    static let letterLinksKey = "transferLetterEntityLinks"
    static let customizationsKey = "transferLetterCustomizations"
    static let templatesKey = "customTransferTemplatesV1"
    static let keys = [linksKey, accountsKey, categoriesKey, letterLinksKey, customizationsKey, templatesKey]

    static func invalid(_ message: String) -> NSError {
        NSError(domain: "EntityRename", code: 1, userInfo: [NSLocalizedDescriptionKey: message])
    }
    static func validatedName(_ input: String) throws -> String {
        let name = input.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !ArabicText.normalize(name).isEmpty, name.count <= 300,
              !name.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else {
            throw invalid(tr("أدخل تسمية صحيحة للجهة، بحد أقصى ٣٠٠ حرف.", "Enter a valid entity name, up to 300 characters."))
        }
        return name
    }
    static func destination(_ input: String, source: String, names: [String]) throws -> String {
        let name = try validatedName(input)
        if names.contains(name) { return name }
        let candidates = Set(names.filter { $0 != source && ArabicText.normalize($0) == ArabicText.normalize(name) })
        guard candidates.count <= 1 else {
            throw invalid(tr("توجد تسميات متشابهة؛ اختر الاسم المطلوب من الجهات المحفوظة.", "Several names match. Choose the intended saved entity."))
        }
        return candidates.first ?? name
    }
    private static func dictionary(_ data: Data?) throws -> [String: String] {
        guard let data, !data.isEmpty else { return [:] }
        return try JSONDecoder().decode([String: String].self, from: data)
    }
    private struct Account: Decodable {
        let entity: String
        let iban: String
        let branch: String
        let localAccount: String?
        var key: String { ArabicText.normalize(entity) }
        var identity: String {
            ArabicText.latinDigits(iban).uppercased().filter { !$0.isWhitespace && $0 != "-" }
                + "|" + (localAccount.map { ArabicText.latinDigits($0).filter(\.isNumber) } ?? "")
        }
    }
    static func settings(source: String, destination: String, current: [String: Data]) throws -> [String: Data] {
        let destination = try validatedName(destination)
        let old = ArabicText.normalize(source), new = ArabicText.normalize(destination)
        var updates: [String: Data] = [:]
        var links = try dictionary(current[linksKey])
        var categories = try dictionary(current[categoriesKey])
        let accounts = try (current[accountsKey].flatMap { $0.isEmpty ? nil : $0 }).map {
            try JSONDecoder().decode([Account].self, from: $0)
        } ?? []
        func account(_ title: String) -> Account? {
            let key = links["deduction|" + ArabicText.normalize(title)] ?? ArabicText.normalize(title)
            return accounts.first { $0.key == key }
        }
        let previous = account(source), target = account(destination)
        if let previous, let target, previous.identity != target.identity {
            throw invalid(tr("للجهتين حسابان مصرفيان مختلفان؛ راجع ربط الحسابات قبل توحيد التسمية. لم تتغير البيانات.", "The entities have different bank accounts. Review their account links before unifying the name. Data was preserved."))
        }
        let chosen = target ?? previous
        if old != new, target == nil, let previous {
            links["deduction|" + new] = previous.key
        }
        // يبقى ربط المصدر السابق حتى إعادة استيراد ملف يستخدم التسمية الأصلية.
        let oldCategory = "\(old)|\(previous?.iban ?? "")|\(ArabicText.normalize(previous?.branch ?? ""))"
        let newCategory = "\(new)|\(chosen?.iban ?? "")|\(ArabicText.normalize(chosen?.branch ?? ""))"
        if let value = categories[oldCategory], oldCategory != newCategory {
            if let existing = categories[newCategory], existing != value {
                throw invalid(tr("تصنيف التحويل مختلف بين الجهتين؛ وحّد التصنيف أولاً ثم أعد المحاولة.", "The entities have different transfer categories. Review their categories before unifying them."))
            }
            categories[newCategory] = value
        }
        if links != (try dictionary(current[linksKey])) { updates[linksKey] = try JSONEncoder().encode(links) }
        if categories != (try dictionary(current[categoriesKey])) { updates[categoriesKey] = try JSONEncoder().encode(categories) }

        // تحديث مراجع القوالب دون استبدال الكلمات داخل المتن أو الحسابات أو الأرشيف.
        func renameTemplate(_ input: [String: Any]) -> [String: Any] {
            var template = input
            if template["source"] as? String == "execution", template["sourceName"] as? String == source {
                template["sourceName"] = destination
            }
            return template
        }
        func renameCustomization(_ input: [String: Any]) -> [String: Any] {
            var value = input
            if value["beneficiarySource"] as? String == source { value["beneficiarySource"] = destination }
            if let template = value["letterTemplate"] as? [String: Any] { value["letterTemplate"] = renameTemplate(template) }
            return value
        }
        for key in [letterLinksKey, customizationsKey, templatesKey] {
            guard let data = current[key], !data.isEmpty else { continue }
            guard var root = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
                throw invalid(tr("تعذّرت قراءة روابط الكتب المحفوظة؛ لم تتغير البيانات.", "Saved letter links could not be read. Data was preserved."))
            }
            if key == letterLinksKey {
                for id in Array(root.keys) {
                    guard var link = root[id] as? [String: Any] else { throw invalid(tr("رابط كتاب غير صالح.", "Invalid letter link.")) }
                    if link["title"] as? String == old { link["title"] = new; root[id] = link }
                }
            } else if key == customizationsKey {
                if let common = root["common"] as? [String: Any] { root["common"] = renameCustomization(common) }
                if let individual = root["individual"] as? [String: [String: Any]] {
                    root["individual"] = individual.mapValues(renameCustomization)
                }
            } else {
                guard root["version"] as? Int == 1, let templates = root["templates"] as? [[String: Any]] else {
                    throw invalid(tr("صيغة قوالب الكتب غير مدعومة؛ لم تتغير البيانات.", "Unsupported letter templates. Data was preserved."))
                }
                root["templates"] = templates.map(renameTemplate)
            }
            let next = try JSONSerialization.data(withJSONObject: root, options: [.sortedKeys])
            let before = try JSONSerialization.data(withJSONObject: JSONSerialization.jsonObject(with: data), options: [.sortedKeys])
            if next != before { updates[key] = next }
        }
        return updates
    }
}

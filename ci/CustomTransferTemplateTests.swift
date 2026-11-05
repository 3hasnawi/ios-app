import Foundation

@main struct CustomTransferTemplateTests {
    static func main() throws {
        var template = CustomTransferTemplate()
        template.title = "كتاب وهمي"
        template.body = "تحويل {{المبلغ}} إلى {{الحساب}} لشهر {{الشهر}}"
        template.source = .execution
        template.sourceName = "جهة وهمية"
        template.account = "123456789012345"
        let encoded = try CustomTransferTemplate.encode([template])
        let decoded = try CustomTransferTemplate.decode(encoded)
        precondition(decoded == [template])
        let rendered = try template.render(["المبلغ": "123", "الحساب": "456", "الشهر": "تموز"])
        precondition(rendered == "تحويل 123 إلى 456 لشهر تموز")
        do { _ = try template.render(["المبلغ": "123"]); fatalError("missing account accepted") } catch {}
        template.body = "{{حقل غير معروف}}"
        do { _ = try template.render([:]); fatalError("unknown field accepted") } catch {}
        do { _ = try CustomTransferTemplate.encode([template, template]); fatalError("duplicate id accepted") } catch {}
        var json = try JSONSerialization.jsonObject(with: encoded) as! [String: Any]
        json["version"] = 2
        do { _ = try CustomTransferTemplate.decode(JSONSerialization.data(withJSONObject: json)); fatalError("future version accepted") } catch {}
        json["version"] = 1
        json["futureKey"] = "data"
        do { _ = try CustomTransferTemplate.decode(JSONSerialization.data(withJSONObject: json)); fatalError("unknown data accepted") } catch {}
        print("Custom transfer template tests passed")
    }
}

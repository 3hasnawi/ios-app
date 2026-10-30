import Foundation

/// يحدد جهات فرع الأنبار التي لا يوجد ربط باسمها في فهرس الشهر، دون الجزم بعدم صدور كتاب.
nonisolated enum TransferArchiveCoverage {
    static func unlinked(entities: [TransferEntity], linkedEntityKeys: Set<String>) -> [TransferEntity] {
        entities.filter { entity in
            entity.category == .anbar && !linkedEntityKeys.contains(ArabicText.normalize(entity.group.title))
        }
    }
}

import Foundation

public enum BugReport {
    /// Builds a user-reviewed draft. It never uploads settings, logs or machine identifiers.
    public static func url(version: String, system: String) -> URL? {
        var components = URLComponents(string: "https://github.com/perf769/actions-ring/issues/new")!
        let body = """
        ## Что произошло

        Опишите проблему.

        ## Как повторить

        1. 
        2. 
        3. 

        ## Ожидаемое поведение

        Как должен работать этот сценарий?

        ## Скриншоты

        При необходимости прикрепите снимок или короткую запись экрана. Скройте личные данные перед публикацией.

        ## Версия и система

        - Actions Ring: \(version)
        - ОС и архитектура: \(system)

        Сообщение будет публичным. Настройки и журналы автоматически не прикладываются.
        """
        components.queryItems = [URLQueryItem(name: "template", value: "bug_report.md"), URLQueryItem(name: "body", value: body)]
        return components.url
    }
}

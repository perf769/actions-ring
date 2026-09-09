import XCTest
@testable import ActionsRingKit

final class BugReportTests: XCTestCase {
    func testReportDraftUsesOnlyOfficialRepositoryAndProvidedSystemDetails() throws {
        let url = try XCTUnwrap(BugReport.url(version: "0.1.0", system: "macOS 15 · arm64"))
        XCTAssertEqual(url.scheme, "https")
        XCTAssertEqual(url.host, "github.com")
        XCTAssertEqual(url.path, "/perf769/actions-ring/issues/new")
        let query = try XCTUnwrap(URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems)
        XCTAssertEqual(query.first { $0.name == "template" }?.value, "bug_report.md")
        let body = try XCTUnwrap(query.first { $0.name == "body" }?.value)
        XCTAssertTrue(body.contains("Actions Ring: 0.1.0"))
        XCTAssertTrue(body.contains("macOS 15 · arm64"))
        XCTAssertFalse(body.contains("/Users/"))
        XCTAssertEqual(query.count, 2)
    }

    func testValuesCannotInjectAdditionalQueryParameters() throws {
        let version = "0.1.0+check & labels=anything#fragment"
        let url = try XCTUnwrap(BugReport.url(version: version, system: "macOS"))
        let query = try XCTUnwrap(URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems)
        XCTAssertEqual(query.count, 2)
        XCTAssertNil(url.fragment)
        XCTAssertTrue(query.first { $0.name == "body" }?.value?.contains(version) == true)
    }
}

import copy
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from zipfile import ZipFile

import publish_microsoft_store as store


IDENTITY = {"Name": "PaulSavvas.RemindersforWindows", "Publisher": "CN=Publisher"}
FILES = {
    "Reminders-for-Windows-1.2.0.0-x64-10.msix": Path("x64.msix"),
    "Reminders-for-Windows-1.2.0.0-arm64-11.msix": Path("ARM64.msix"),
}
UPLOAD_URL = "https://ingestion.blob.core.windows.net/zip?sig=example"
RELEASE_URL = "https://github.com/Psavvas/Reminders-for-Windows/releases/tag/v1.2.0"


class ReleaseNotesTests(unittest.TestCase):
    def test_customer_changes_exclude_installation_and_generated_notes(self):
        text = "## What’s new in this version\n\n- **Move** a reminder to another list.\n\n"
        text += "## Installing\nRun the installer.\n\n## What's Changed\n* Update dependencies"
        self.assertEqual(store.release_notes(text, RELEASE_URL), "- Move a reminder to another list.")

    def test_github_generated_changes(self):
        text = "## Signing in\nChoose a sign-in method.\n\n## What's Changed\n"
        text += "* Fix sync by @octocat in https://github.com/Psavvas/Reminders-for-Windows/pull/42\n"
        text += "## New Contributors\n* @octocat made a contribution\n"
        self.assertEqual(store.release_notes(text, RELEASE_URL), "- Fix sync")

    def test_freeform_updates_survive_nested_install_sections(self):
        text = "General improvements.\n\n## Installing\n### Installing and updating\nRun Setup\n\n"
        text += "## Sync fixes\nReminder lists now sync after reconnecting."
        self.assertEqual(store.release_notes(text, RELEASE_URL),
                         "General improvements.\n\nSync fixes\nReminder lists now sync after reconnecting.")

    def test_limits_unicode_notes_and_links_to_original(self):
        notes = store.release_notes("✅ Improved sync 🚀\n" * 200, RELEASE_URL)
        self.assertLessEqual(len(notes.encode("utf-16-le")) // 2, store.NOTES_LIMIT)
        self.assertTrue(notes.endswith(RELEASE_URL))

    def test_empty_notes_stop_the_submission(self):
        with self.assertRaisesRegex(store.PublishError, "Add change notes"):
            store.release_notes("## Installing\nRun Setup", RELEASE_URL)


class SubmissionTests(unittest.TestCase):
    def setUp(self):
        self.app = {
            "packageIdentityName": IDENTITY["Name"],
            "publisherName": IDENTITY["Publisher"],
            "lastPublishedApplicationSubmission": {"id": "1"},
        }
        self.published = {
            "listings": {
                "en-US": {"baseListing": {"description": "Reminders", "releaseNotes": "Old notes"}},
                "en-gb": {"baseListing": {"releaseNotes": "Old notes"}},
                "fr-fr": {"baseListing": {"releaseNotes": "Notes en français"}},
            },
            "pricing": {"priceId": "Free"},
            "applicationPackages": [
                {"fileName": f"old-{arch}.msix", "architecture": arch, "version": "1.1.0.1",
                 "fileStatus": "Uploaded"} for arch in ("X64", "Arm64", "X86")
            ],
        }
        self.draft = dict(copy.deepcopy(self.published), id="2", fileUploadUrl=UPLOAD_URL,
                          status="PendingCommit", targetPublishMode="Manual")
        self.calls = []

    def call(self, method, path, payload=None):
        self.calls.append((method, path))
        responses = {
            ("GET", ""): self.app,
            ("GET", "/submissions/1"): self.published,
            ("GET", "/submissions/2"): self.draft,
            ("POST", "/submissions"): self.draft,
            ("PUT", "/submissions/2"): payload,
            ("POST", "/submissions/2/commit"): {"status": "CommitStarted"},
            ("GET", "/submissions/2/status"): {"status": "Certification"},
        }
        if method == "PUT":
            self.body = payload
        return responses[method, path]

    @patch.object(store, "upload_zip")
    def test_submits_both_packages_and_notes_in_one_update(self, upload):
        store.submit(self.call, IDENTITY, (1, 2, 0), FILES, "Improved sync", False)

        self.assertEqual(self.calls[-4:], [
            ("POST", "/submissions"), ("PUT", "/submissions/2"),
            ("POST", "/submissions/2/commit"), ("GET", "/submissions/2/status"),
        ])
        upload.assert_called_once_with(UPLOAD_URL, FILES)
        self.assertEqual(self.body["targetPublishMode"], "Immediate")
        self.assertEqual(self.body["pricing"], self.published["pricing"])
        for locale in ("en-US", "en-gb"):
            self.assertEqual(self.body["listings"][locale]["baseListing"]["releaseNotes"], "Improved sync")
        self.assertEqual(self.body["listings"]["fr-fr"], self.published["listings"]["fr-fr"])
        self.assertEqual(self.body["listings"]["en-US"]["baseListing"]["description"], "Reminders")
        status_by_name = {p["fileName"]: p["fileStatus"] for p in self.body["applicationPackages"]}
        self.assertEqual(status_by_name, {
            "old-X64.msix": "PendingDelete", "old-Arm64.msix": "PendingDelete",
            "old-X86.msix": "Uploaded", **dict.fromkeys(FILES, "PendingUpload"),
        })

    def test_leaves_an_unrelated_draft_alone(self):
        self.app["pendingApplicationSubmission"] = {"id": "2"}
        with self.assertRaisesRegex(store.PublishError, "already has submission 2"):
            store.submit(self.call, IDENTITY, (1, 2, 0), FILES, "Improved sync", False)
        self.assertTrue(all(method == "GET" for method, _ in self.calls))

    @patch.object(store, "upload_zip")
    def test_resumes_own_draft_without_adding_duplicate_packages(self, upload):
        self.app["pendingApplicationSubmission"] = {"id": "2"}
        self.draft["applicationPackages"].extend(
            {"fileName": name, "fileStatus": "PendingUpload"} for name in FILES
        )
        store.submit(self.call, IDENTITY, (1, 2, 0), FILES, "Improved sync", False)
        self.assertNotIn(("POST", "/submissions"), self.calls)
        pending_uploads = [p["fileName"] for p in self.body["applicationPackages"]
                           if p["fileStatus"] == "PendingUpload"]
        self.assertCountEqual(pending_uploads, FILES)
        upload.assert_called_once()

    def test_published_store_revision_and_older_releases_are_noops(self):
        for version in ((1, 1, 0), (1, 0, 0)):
            with self.subTest(version=version):
                store.submit(self.call, IDENTITY, version, FILES, "Improved sync", False)
        self.assertTrue(all(method == "GET" for method, _ in self.calls))

    def test_check_only_has_no_store_writes(self):
        store.submit(self.call, IDENTITY, (1, 2, 0), FILES, "Improved sync", True)
        self.assertTrue(all(method == "GET" for method, _ in self.calls))

    def test_partner_center_rejection_is_reported(self):
        def status_call(method, path):
            return {"status": "PreProcessingFailed"}

        with self.assertRaisesRegex(store.PublishError, "submission 2 is PreProcessingFailed"):
            store.wait_for_ingestion(status_call, "/submissions/2", "2")


class ArchiveTests(unittest.TestCase):
    def test_upload_stream_contains_both_unmodified_msix_files(self):
        with tempfile.TemporaryDirectory() as directory:
            payloads = {name: architecture.encode() for name, architecture in
                        zip(FILES, ("x64", "arm64"), strict=True)}
            files = {name: Path(directory) / name for name in payloads}
            for name, path in files.items():
                path.write_bytes(payloads[name])

            def receive(method, url, label, *, data, headers, timeout):
                self.assertEqual(method, "PUT")
                self.assertEqual(url, UPLOAD_URL + "&api-version=2019-12-12")
                self.assertEqual(headers["x-ms-blob-type"], "BlockBlob")
                with ZipFile(data) as archive:
                    self.assertCountEqual(archive.namelist(), files)
                    for name, payload in payloads.items():
                        self.assertEqual(archive.read(name), payload)
                return io.BytesIO()

            with patch.object(store, "request", side_effect=receive):
                store.upload_zip(UPLOAD_URL, files)


if __name__ == "__main__":
    unittest.main()

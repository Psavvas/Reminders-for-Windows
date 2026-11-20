"""Submit the two MSIX assets of a published GitHub release to Partner Center."""

from email.utils import formatdate
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.parse import parse_qs, quote, urlencode, urlsplit
from urllib.request import Request, urlopen
from xml.etree import ElementTree
from zipfile import BadZipFile, ZipFile


STORE = "https://manage.devcenter.microsoft.com"
ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "src/Reminders.Package/Package.appxmanifest"
ARCHITECTURES = {"x64": "x64", "ARM64": "arm64"}
NOTES_LIMIT = 1500
HEADING = re.compile(r"^ {0,3}(#{1,6})[ \t]+(.+?)\s*$", re.MULTILINE)
BOILERPLATE = {"signing in", "installing", "installing and updating", "validation", "new contributors"}
ACCEPTED = {"Certification", "Release", "Publishing", "Published"}


class PublishError(Exception):
    pass


def require_env(name):
    value = os.environ.get(name)
    if not value:
        raise PublishError(f"Configure {name} in the GitHub microsoft-store environment.")
    return value


def request(method, url, label, *, token=None, headers=None, data=None, timeout=60):
    req = Request(url, data=data, method=method, headers=headers or {})
    if token:
        # GitHub can redirect an asset download to blob storage.
        req.add_unredirected_header("Authorization", f"Bearer {token}")
    try:
        return urlopen(req, timeout=timeout)
    except HTTPError as exc:
        raise PublishError(f"{label} failed (HTTP {exc.code}).") from None
    except (URLError, TimeoutError):
        raise PublishError(f"{label} failed to connect or timed out.") from None


def json_request(method, url, label, *, token=None, payload=None):
    if payload is None:
        response = request(method, url, label, token=token)
    else:
        response = request(method, url, label, token=token, data=json.dumps(payload).encode(),
                           headers={"Content-Type": "application/json"})
    with response:
        return json.load(response)


def version_parts(version, count):
    if not re.fullmatch(r"[0-9]{1,5}(?:\.[0-9]{1,5}){" + str(count - 1) + r"}", version):
        raise PublishError("Expected a numeric three-part tag or four-part package version.")
    parts = tuple(map(int, version.split(".")))
    if any(part > 65535 for part in parts):
        raise PublishError("Package version components must fit Microsoft's version limits.")
    return parts


def verify_tag(tag, release_id):
    version = tag.removeprefix("v")
    parts = version_parts(version, 3)
    if tag != "v" + ".".join(map(str, parts)):
        raise PublishError("Use a release tag in the form vMAJOR.MINOR.PATCH.")
    if not parts[0]:
        raise PublishError("Microsoft Store submissions require a nonzero major version.")
    ref = f"refs/tags/{tag}^{{commit}}"
    result = subprocess.run(["git", "rev-parse", "--verify", ref], cwd=ROOT,
                            capture_output=True, text=True)
    if result.returncode:
        raise PublishError(f"Release tag {tag} was not found on the checkout.")
    sha = result.stdout.strip()
    if release_id and sha != os.environ["GITHUB_SHA"]:
        raise PublishError(f"Release tag {tag} no longer points at the published commit.")
    if subprocess.run(["git", "merge-base", "--is-ancestor", sha, "HEAD"], cwd=ROOT).returncode:
        raise PublishError(f"Release tag {tag} must point to a commit on main.")
    return parts


def release_notes(body, release_url):
    headings = list(HEADING.finditer(body))

    def title(heading):
        return heading[2].strip(" #").lower().replace("’", "'")

    def section_end(index):
        return next((heading.start() for heading in headings[index + 1:]
                     if len(heading[1]) <= len(headings[index][1])), len(body))

    for titles in ({"what's new", "what's new in this version"},
                   {"what's changed", "changes", "changelog"}):
        index = next((i for i, heading in enumerate(headings) if title(heading) in titles), None)
        if index is not None:
            body = body[headings[index].end():section_end(index)]
            break
    else:
        # A hand-written release can also put its changelog directly in the body.
        sections = []
        start = 0
        for index, heading in enumerate(headings):
            if heading.start() >= start and title(heading) in BOILERPLATE:
                sections.append(body[start:heading.start()])
                start = section_end(index)
        body = "".join(sections) + body[start:]

    body = HEADING.sub(r"\2", body)
    body = re.sub(r"<!--.*?-->", "", body, flags=re.DOTALL)
    body = re.sub(r"(?im)^\s*\*{0,2}full changelog\*{0,2}:.*$", "", body)
    body = re.sub(r" by @\S+ in https://github\.com/\S+/pull/\d+\s*$", "", body,
                  flags=re.MULTILINE)
    body = re.sub(r"\[([^\]]+)\]\(https?://[^)]+\)", r"\1", body)
    body = re.sub(r"\*\*(.+?)\*\*|__(.+?)__|`([^`]+)`",
                  lambda match: next(group for group in match.groups() if group is not None), body)
    body = re.sub(r"(?m)^([ \t]*)\* ", r"\1- ", body).strip()
    if not body:
        raise PublishError("Add change notes to the published GitHub release and retry.")

    # Count UTF-16 code units so supplementary Unicode characters fit as well.
    if len(body.encode("utf-16-le")) // 2 > NOTES_LIMIT:
        suffix = f"…\n\nFull release notes: {release_url}"
        budget = NOTES_LIMIT - len(suffix.encode("utf-16-le")) // 2
        excerpt = body.encode("utf-16-le")[:budget * 2].decode("utf-16-le", errors="ignore")
        boundary = excerpt.rfind("\n")
        if boundary < budget // 2:
            boundary = excerpt.rfind(" ")
        body = excerpt[:boundary].rstrip() + suffix if boundary > 0 else excerpt + suffix
    return body


def download_packages(release, github_url, github_token, directory, version):
    source = ElementTree.parse(MANIFEST).getroot()
    identity = source.find("{*}Identity").attrib
    publisher = source.find("{*}Properties/{*}PublisherDisplayName").text
    files = {}
    for name_suffix, architecture in ARCHITECTURES.items():
        name = f"Reminders-for-Windows-{name_suffix}.msix"
        assets = [asset for asset in release["assets"] if asset["name"] == name]
        if len(assets) != 1 or assets[0]["state"] != "uploaded" or assets[0]["size"] <= 0:
            raise PublishError(f"The release needs exactly one uploaded, non-empty {name}.")
        asset = assets[0]
        path = directory / f"Reminders-for-Windows-{version}-{architecture}-{asset['id']}.msix"
        digest = hashlib.sha256()
        with request("GET", f"{github_url}/releases/assets/{asset['id']}", f"Download {name}",
                     token=github_token, headers={"Accept": "application/octet-stream"}) as response:
            with path.open("wb") as package:
                while chunk := response.read(1024 * 1024):
                    package.write(chunk)
                    digest.update(chunk)
        if path.stat().st_size != asset["size"] or (
            asset.get("digest") and asset["digest"] != f"sha256:{digest.hexdigest()}"
        ):
            raise PublishError(f"{name} does not match its GitHub release asset.")
        with ZipFile(path) as package, package.open("AppxManifest.xml") as manifest:
            actual = ElementTree.parse(manifest).getroot()
        expected = {"Name": identity["Name"], "Publisher": identity["Publisher"],
                    "ProcessorArchitecture": architecture, "Version": version}
        if (any(actual.find("{*}Identity").get(key) != value for key, value in expected.items())
                or actual.findtext("{*}Properties/{*}PublisherDisplayName") != publisher):
            raise PublishError(f"{name} has the wrong Store publisher, version, or architecture.")
        files[path.name] = path
    return identity, files


def get_store_token():
    tenant = quote(require_env("STORE_TENANT_ID"), safe="")
    form = urlencode({
        "grant_type": "client_credentials",
        "client_id": require_env("STORE_CLIENT_ID"),
        "client_secret": require_env("STORE_CLIENT_SECRET"),
        "resource": STORE,
    })
    with request("POST", f"https://login.microsoftonline.com/{tenant}/oauth2/token",
                 "Authenticate with Microsoft Entra", data=form.encode(),
                 headers={"Content-Type": "application/x-www-form-urlencoded"}) as response:
        return json.load(response)["access_token"]


def english_listings(submission):
    return [listing["baseListing"] for locale, listing in submission["listings"].items()
            if locale.lower().split("-")[0] == "en"]


def update_submission(submission, files, notes):
    body = {key: value for key, value in submission.items()
            if key not in {"id", "friendlyName", "status", "statusDetails", "fileUploadUrl", "targetPublishDate"}}
    body["targetPublishMode"] = "Immediate"
    for listing in english_listings(body):
        listing["releaseNotes"] = notes

    packages = [package for package in body["applicationPackages"] if package["fileName"] not in files]
    for package in packages:
        if package.get("architecture", "").lower() in ARCHITECTURES.values():
            package["fileStatus"] = "PendingDelete"
    for name in files:
        packages.append({"fileName": name, "fileStatus": "PendingUpload",
                         "minimumDirectXVersion": "None", "minimumSystemRam": "None"})
    body["applicationPackages"] = packages
    return body


def upload_zip(url, files):
    target = urlsplit(url)
    if (target.scheme != "https" or not (target.hostname or "").endswith(".blob.core.windows.net")
            or not target.query or target.fragment):
        raise PublishError("Partner Center did not return a valid Azure Blob upload URL.")
    if "api-version" not in parse_qs(target.query):
        url += "&api-version=2019-12-12"
    archive = next(iter(files.values())).parent / "submission.zip"
    with ZipFile(archive, "w") as bundle:
        for name, path in files.items():
            bundle.write(path, name)
    with archive.open("rb") as data, request(
        "PUT", url, "Upload both MSIX files to Azure Blob Storage", data=data, timeout=240,
        headers={"Content-Type": "application/zip", "Content-Length": str(archive.stat().st_size),
                 "x-ms-blob-type": "BlockBlob", "x-ms-date": formatdate(usegmt=True)},
    ):
        pass


def report(message):
    print(message, flush=True)
    if path := os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(path, "a", encoding="utf-8") as summary:
            summary.write(message + "\n\n")


def wait_for_ingestion(call, path, submission):
    for _ in range(30):
        status = call("GET", f"{path}/status")["status"]
        if status in ACCEPTED:
            message = f"Partner Center submission {submission} is {status}."
            if status != "Published":
                message += " Microsoft publishes automatically after certification."
            report(message)
            return
        if status not in {"PendingCommit", "CommitStarted", "PreProcessing", "PendingPublication"}:
            raise PublishError(f"Partner Center submission {submission} is {status}. "
                               "Check its report in Partner Center.")
        time.sleep(10)
    raise PublishError(f"Partner Center submission {submission} is still {status}. "
                       "Check its status in Partner Center or rerun the workflow.")


def submit(call, identity, version, files, notes, check_only):
    app = call("GET", "")
    if (app["packageIdentityName"] != identity["Name"] or app["publisherName"] != identity["Publisher"]):
        raise PublishError("STORE_PRODUCT_ID does not match the packages' Partner Center identity.")
    published_id = (app.get("lastPublishedApplicationSubmission") or {}).get("id")
    if not published_id:
        raise PublishError("Complete the first publication and age rating in Partner Center.")
    published = call("GET", f"/submissions/{published_id}")
    if not english_listings(published):
        raise PublishError("The published Store submission needs an English listing for release notes.")
    versions = {}
    for package in published["applicationPackages"]:
        architecture = package["architecture"].lower()
        previous = version_parts(package["version"], 4)[:3]
        versions[architecture] = max(previous, versions.get(architecture, previous))
    pending_id = (app.get("pendingApplicationSubmission") or {}).get("id")
    if check_only:
        report("Verified both release packages, the Store notes, and access to the published Store app."
               + (f" Partner Center has pending submission {pending_id}." if pending_id else ""))
        return
    if versions and version < max(versions.values()):
        report("The Store already has a newer package version.")
        return
    if all(versions.get(architecture) == version for architecture in ARCHITECTURES.values()):
        report("Both architectures of this version are already published in the Store.")
        return
    if version in versions.values():
        raise PublishError("This package version is already in use in the Store. Bump the release version.")

    if pending_id:
        path = f"/submissions/{pending_id}"
        submission = call("GET", path)
        if not files.keys() <= {package["fileName"] for package in submission["applicationPackages"]}:
            raise PublishError(f"Partner Center already has submission {pending_id}. "
                               "Resolve it before retrying this GitHub release.")
        if submission["status"] != "PendingCommit":
            wait_for_ingestion(call, path, pending_id)
            return
        report(f"Resuming Partner Center submission {pending_id}.")
    else:
        submission = call("POST", "/submissions")
        pending_id = submission["id"]
        path = f"/submissions/{pending_id}"
        report(f"Created Partner Center submission {pending_id}.")

    updated = call("PUT", path, update_submission(submission, files, notes))
    listings = english_listings(updated)
    pending_uploads = {package["fileName"] for package in updated["applicationPackages"]
                       if package["fileStatus"] == "PendingUpload"}
    if (updated["targetPublishMode"] != "Immediate" or pending_uploads != files.keys()
            or not listings or any(listing.get("releaseNotes") != notes for listing in listings)):
        raise PublishError(f"Partner Center did not save both packages, notes, and publishing mode "
                           f"for submission {pending_id}.")
    upload_zip(updated.get("fileUploadUrl") or submission["fileUploadUrl"], files)
    call("POST", f"{path}/commit")
    wait_for_ingestion(call, path, pending_id)


def main():
    tag = os.environ["RELEASE_TAG"]
    release_id = os.environ.get("RELEASE_ID")
    version = verify_tag(tag, release_id)
    repository = os.environ["GITHUB_REPOSITORY"]
    github_url = f"{os.environ['GITHUB_API_URL']}/repos/{repository}"
    github_token = os.environ["GH_TOKEN"]
    release_path = release_id or f"tags/{quote(tag, safe='')}"
    release = json_request("GET", f"{github_url}/releases/{release_path}", "Read GitHub release",
                           token=github_token)
    if release["draft"] or release["prerelease"] or release["tag_name"] != tag:
        raise PublishError("Select a published stable GitHub release with the requested tag.")
    notes = release_notes(release.get("body") or "", f"{os.environ['GITHUB_SERVER_URL']}/{repository}/releases/tag/{tag}")
    app_id = quote(require_env("STORE_PRODUCT_ID"), safe="")

    with tempfile.TemporaryDirectory(prefix="store-release-") as directory:
        identity, files = download_packages(release, github_url, github_token, Path(directory),
                                            f"{tag[1:]}.0")
        store_token = get_store_token()

        def call(method, path, payload=None):
            return json_request(method, f"{STORE}/v1.0/my/applications/{app_id}{path}",
                                f"Partner Center {method} {path or 'app'}", token=store_token, payload=payload)

        submit(call, identity, version, files, notes, os.environ.get("CHECK_ONLY") == "true")


if __name__ == "__main__":
    try:
        main()
    except PublishError as exc:
        raise SystemExit(str(exc)) from None
    except (BadZipFile, ElementTree.ParseError, KeyError, OSError, ValueError):
        raise SystemExit("Could not read the release, package manifest, or Store response. "
                         "Check the workflow log and Partner Center before retrying.") from None

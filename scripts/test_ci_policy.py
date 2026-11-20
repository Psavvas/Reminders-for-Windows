"""CI trust-boundary regression checks. Run with Python and PyYAML installed."""
from pathlib import Path
import re
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[1]


def load(name):
    # BaseLoader keeps YAML's `on` key a string (YAML 1.1 treats it as True).
    return yaml.load((ROOT / '.github/workflows' / name).read_text(), Loader=yaml.BaseLoader)


def validate(dispatcher, pipeline):
    assert set(dispatcher['on']) == {'push', 'pull_request', 'workflow_dispatch'}
    assert set(pipeline['on']) == {'workflow_call'}
    assert dispatcher['permissions'] == {'contents': 'read'}
    assert dispatcher['cache-mode'] == 'read'
    jobs = dispatcher['jobs']
    assert set(jobs) == {'ci', 'main', 'tag', 'release'}
    assert jobs['ci']['cache-mode'] == 'read'
    assert jobs['ci']['with'].get('save-cache', 'false') == 'false'
    assert jobs['ci']['with'].get('release', 'false') == 'false'
    assert jobs['main']['cache-mode'] == 'write'
    assert jobs['main']['if'] == "github.event_name == 'push' && github.ref == 'refs/heads/main'"
    assert jobs['main']['with']['save-cache'] == 'true'
    assert jobs['tag']['cache-mode'] == 'none'
    assert jobs['tag']['with']['use-cache'] == 'false'
    assert jobs['tag']['with'].get('save-cache', 'false') == 'false'
    assert jobs['tag']['if'] == "github.event_name == 'push' && startsWith(github.ref, 'refs/tags/v')"
    for name in ('ci', 'main', 'tag'):
        assert jobs[name]['uses'] == './.github/workflows/native-build.yml'
        assert jobs[name].get('permissions', {'contents': 'read'}) == {'contents': 'read'}
    release = jobs['release']
    assert release['cache-mode'] == 'none'
    assert release['needs'] == 'tag'
    assert release['if'] == jobs['tag']['if']
    assert release['permissions'] == {'contents': 'write'}
    for step in release['steps']:
        if step.get('uses'):
            assert re.fullmatch(r'[\w-]+/[\w/-]+@[0-9a-f]{40}', step['uses'])
    assert not any('checkout@' in step.get('uses', '') or 'cache' in step.get('uses', '') for step in release['steps'])
    download = next(step for step in release['steps'] if 'download-artifact@' in step.get('uses', ''))
    assert download['with']['pattern'] == 'Reminders-for-Windows-*-${{ github.sha }}'
    assert not {'run-id', 'repository', 'github-token'} & download['with'].keys()
    assert pipeline['permissions'] == {'contents': 'read'}
    assert set(pipeline['jobs']) == {'windows', 'backend-checks', 'audit'}
    for job in pipeline['jobs'].values():
        assert job['permissions'] == {'contents': 'read'}
        assert 'cache-mode' not in job  # Must inherit the caller's cap.
        for step in job['steps']:
            action = step.get('uses', '')
            if action:
                assert re.fullmatch(r'[\w-]+/[\w/-]+@[0-9a-f]{40}', action), action
            if 'checkout@' in action:
                assert step['with']['persist-credentials'] == 'false'
            if 'actions/cache/' in action:
                assert step['with']['path'] == '.nuget/packages'
                if '/save@' in action:
                    assert 'inputs.save-cache' in step['if']
                    assert "github.event_name == 'push'" in step['if']
                    assert "github.ref == 'refs/heads/main'" in step['if']
                else:
                    assert step['if'] == 'inputs.use-cache'
                    assert 'restore-keys' not in step['with']
                    assert '${{ matrix.architecture }}' in step['with']['key']
                    assert 'packages.{0}.lock.json' in step['with']['key']
            assert '${{ github.' not in step.get('run', ''), 'Untrusted GitHub data embedded in executable script'
    publish = next(step['run'] for step in pipeline['jobs']['windows']['steps'] if step.get('name') == 'Build and publish native app')
    assert '-LockedRestore' in publish


class CiPolicyTests(unittest.TestCase):
    def setUp(self):
        self.dispatcher = load('build.yml')
        self.pipeline = load('native-build.yml')

    def test_current_policy(self):
        validate(self.dispatcher, self.pipeline)

    def test_store_deployment_is_isolated_from_builds(self):
        store = load('microsoft-store.yml')
        assert set(store['on']) == {'release', 'workflow_dispatch'}
        assert store['on']['release']['types'] == ['published']
        assert store['permissions'] == {'contents': 'read'}
        assert store['cache-mode'] == 'none'
        job = store['jobs']['submit']
        assert job['environment'] == 'microsoft-store'
        assert '!github.event.release.prerelease' in job['if']
        assert "github.ref == 'refs/heads/main'" in job['if']
        checkout, setup, submit = job['steps']
        assert checkout['with']['ref'] == 'refs/heads/main'
        assert checkout['with']['persist-credentials'] == 'false'
        for step in (checkout, setup):
            assert re.fullmatch(r'[\w-]+/[\w/-]+@[0-9a-f]{40}', step['uses'])
            assert 'secrets.' not in str(step)
        assert submit['run'] == 'python scripts/publish_microsoft_store.py'

    def test_rejects_release_cache_restore(self):
        self.dispatcher['jobs']['tag']['with']['use-cache'] = 'true'
        with self.assertRaises(AssertionError):
            validate(self.dispatcher, self.pipeline)

    def test_rejects_pr_cache_write(self):
        self.dispatcher['jobs']['ci']['cache-mode'] = 'write'
        with self.assertRaises(AssertionError):
            validate(self.dispatcher, self.pipeline)

    def test_rejects_privileged_build(self):
        self.pipeline['jobs']['windows']['permissions']['contents'] = 'write'
        with self.assertRaises(AssertionError):
            validate(self.dispatcher, self.pipeline)

    def test_rejects_bypassing_release_checks(self):
        self.dispatcher['jobs']['release']['needs'] = 'ci'
        with self.assertRaises(AssertionError):
            validate(self.dispatcher, self.pipeline)

    def test_rejects_unpinned_action(self):
        self.pipeline['jobs']['audit']['steps'][0]['uses'] = 'actions/checkout@v5'
        with self.assertRaises(AssertionError):
            validate(self.dispatcher, self.pipeline)

    def test_rejects_unpinned_release_action(self):
        self.dispatcher['jobs']['release']['steps'][-1]['uses'] = 'softprops/action-gh-release@v2'
        with self.assertRaises(AssertionError):
            validate(self.dispatcher, self.pipeline)

    def test_rejects_cross_run_artifacts(self):
        self.dispatcher['jobs']['release']['steps'][0]['with']['run-id'] = '123'
        with self.assertRaises(AssertionError):
            validate(self.dispatcher, self.pipeline)


if __name__ == '__main__':
    unittest.main()

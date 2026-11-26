#!/usr/bin/env python3
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, '..'))

ROOT_TYPE = 'WorkloadDesiredSpec'

HASHER = 'src/Reeve.Workloads.Domain/DesiredSpec/SpecHasher.cs'

RECORD_ROOTS = ['src/Reeve.Workloads.Abstractions']


def _skip_literal(text, i):
    n = len(text)
    start = i
    while i < n and text[i] in '$@':
        i += 1
    if i >= n or text[i] not in '"\'':
        return None
    interpolated = '$' in text[start:i]
    verbatim = '@' in text[start:i]

    if text[i] == "'":
        i += 1
        while i < n and text[i] != "'":
            i += 2 if text[i] == '\\' else 1
        return min(i + 1, n)

    fence = i
    while fence < n and text[fence] == '"':
        fence += 1
    quotes = fence - i

    if quotes >= 3:
        marker = '"' * quotes
        j = fence
        while j < n:
            k = text.find(marker, j)
            if k < 0:
                return n
            end = k
            while end < n and text[end] == '"':
                end += 1
            if end - k == quotes:
                return end
            j = end
        return n
    if quotes == 2:
        return i + 2

    i += 1
    depth = 0
    while i < n:
        c = text[i]
        if interpolated and c == '{':
            if i + 1 < n and text[i + 1] == '{':
                i += 2
                continue
            depth += 1
            i += 1
            continue
        if interpolated and depth > 0:
            if c == '}':
                depth -= 1
                i += 1
                continue
            nested = _skip_literal(text, i)
            if nested is not None and nested > i:
                i = nested
                continue
            i += 1
            continue
        if c == '\\' and not verbatim:
            i += 2
            continue
        if c == '"':
            if verbatim and i + 1 < n and text[i + 1] == '"':
                i += 2
                continue
            return i + 1
        i += 1
    return n


def blank_bodies(text):
    out = list(text)
    n = len(text)
    unterminated = []

    def blank(a, b):
        for k in range(a, min(b, n)):
            if out[k] != '\n':
                out[k] = ' '

    for match in re.finditer(r'^[ \t]*#[^\n]*', text, re.M):
        blank(match.start(), match.end())
    text = ''.join(out)

    i = 0
    while i < n:
        if text.startswith('//', i):
            end = text.find('\n', i)
            end = n if end < 0 else end
            blank(i, end)
            i = end
        elif text.startswith('/*', i):
            end = text.find('*/', i)
            if end < 0:
                unterminated.append(text.count('\n', 0, i) + 1)
                blank(i, n)
                break
            blank(i, end + 2)
            i = end + 2
        elif text[i] in '"\'$@':
            end = _skip_literal(text, i)
            if end is None:
                i += 1
                continue
            if end >= n and not text[n - 1:n].endswith(('"', "'")):
                unterminated.append(text.count('\n', 0, i) + 1)
            blank(i, end)
            i = max(end, i + 1)
        else:
            i += 1
    return ''.join(out), unterminated


RECORD_DECL = re.compile(r'\brecord\s+(?P<name>[A-Z][A-Za-z0-9_]*)\s*(?P<tail>[({;])')

PROP_ACCESSOR = re.compile(
    r'^[ \t]*public\s+(?:required\s+)?(?P<type>[^\s;=].*?)\s+(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*\{\s*get\b', re.M)
PROP_EXPRESSION = re.compile(
    r'^[ \t]*public\s+(?:required\s+)?(?P<type>[^\s;=].*?)\s+(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*=>', re.M)

COLLECTION = re.compile(r'^(?:IReadOnlyList|IReadOnlyCollection|IReadOnlySet|IList|ICollection|IEnumerable|List|HashSet)<(?P<of>.+)>$')
DICTIONARY = re.compile(r'^(?:IReadOnlyDictionary|IDictionary|Dictionary|SortedDictionary)<[^,]+,\s*(?P<of>.+)>$')


def unwrap(type_name):
    previous = None
    current = type_name.strip()
    while current != previous:
        previous = current
        current = current.rstrip('?').strip()
        for pattern in (COLLECTION, DICTIONARY):
            match = pattern.match(current)
            if match:
                current = match.group('of').strip()
                break
    return current


def brace_span(text, start):
    i = text.find('{', start)
    if i < 0:
        return None
    depth = 0
    while i < len(text):
        if text[i] == '{':
            depth += 1
        elif text[i] == '}':
            depth -= 1
            if depth == 0:
                return (text.find('{', start), i)
        i += 1
    return None


def parse_records(text):
    blanked, unterminated = blank_bodies(text)
    records = {}
    for match in RECORD_DECL.finditer(blanked):
        name = match.group('name')
        if match.group('tail') == '(':
            records[name] = {'props': {}, 'positional': True}
            continue
        if match.group('tail') == ';':
            continue
        span = brace_span(blanked, match.end() - 1)
        if span is None:
            continue
        body = blanked[span[0]:span[1]]
        inner = [m.start() for m in RECORD_DECL.finditer(body)]
        if inner:
            body = body[:inner[0]]
        props = {}
        for pattern in (PROP_ACCESSOR, PROP_EXPRESSION):
            for prop in pattern.finditer(body):
                props[prop.group('name')] = prop.group('type').strip()
        records[name] = {'props': props, 'positional': False}
    return records, unterminated


IS_PATTERN = re.compile(r'(?P<lhs>\S+)\s+is\s+(?:not\s+)?\{\s*\}\s*(?P<name>[A-Za-z_][A-Za-z0-9_]*)')
FOREACH_PATTERN = re.compile(r'foreach\s*\(\s*var\s+(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s+in\s+(?P<lhs>\S+)')
TRAILING_PATH = re.compile(r'(?:^|[^A-Za-z0-9_.?])(?P<path>[A-Za-z_][A-Za-z0-9_]*(?:\??\.[A-Za-z_][A-Za-z0-9_]*)*)$')
LEADING_PATH = re.compile(r'^(?P<path>[A-Za-z_][A-Za-z0-9_]*(?:\??\.[A-Za-z_][A-Za-z0-9_]*)*)')

# `spec-hash-excluded: Type.Property -- reason`. Read from the RAW text: the blanker would erase it.
EXCLUSION = re.compile(
    r'//\s*spec-hash-excluded:\s*(?P<type>[A-Z][A-Za-z0-9_]*)\.(?P<prop>[A-Za-z_][A-Za-z0-9_]*)\s*(?:--|—)\s*(?P<reason>\S.*?)\s*$',
    re.M)

MIN_REASON = 20


def parse_exclusions(text):
    exclusions, unreasoned = {}, []
    for match in EXCLUSION.finditer(text):
        line = text.count('\n', 0, match.start()) + 1
        reason = match.group('reason').strip()
        key = (match.group('type'), match.group('prop'))
        if len(reason) < MIN_REASON:
            unreasoned.append((line, f"{key[0]}.{key[1]}", reason))
            continue
        exclusions[key] = (reason, line)
    loose = len(re.findall(r'//\s*spec-hash-excluded:', text))
    return exclusions, unreasoned, loose


def walk(records, type_name, segments):
    current = type_name
    for segment in segments:
        record = records.get(current)
        if record is None or segment not in record['props']:
            return
        yield (current, segment)
        current = unwrap(record['props'][segment])


def parse_hasher(text, records):
    blanked, unterminated = blank_bodies(text)
    bindings, reads, unresolved = {}, set(), []

    for name in records:
        for match in re.finditer(rf'\b{name}\??\s+(?P<var>[a-z_][A-Za-z0-9_]*)\s*[,)=;]', blanked):
            bindings.setdefault(match.group('var'), name)

    if ROOT_TYPE not in records:
        unresolved.append((0, f"the root type {ROOT_TYPE} is not declared in the scanned tree"))

    def resolve(lhs, leading=False):
        found = (LEADING_PATH if leading else TRAILING_PATH).search(lhs.strip())
        if found is None:
            return None
        path = found.group('path')
        segments = [s.strip() for s in re.sub(r'\s*\??\.\s*', '.', path).split('.')]
        head, rest = segments[0], segments[1:]
        start = bindings.get(head)
        if start is None:
            return None
        current = start
        for owner, prop in walk(records, current, rest):
            reads.add((owner, prop))
            current = unwrap(records[owner]['props'][prop])
        return current

    for _ in range(2):
        for pattern, is_element in ((IS_PATTERN, False), (FOREACH_PATTERN, True)):
            for match in pattern.finditer(blanked):
                resolved = resolve(match.group('lhs'), leading=is_element)
                if resolved is None:
                    continue
                bindings.setdefault(match.group('name'), unwrap(resolved) if is_element else resolved)

    for pattern, is_element in ((IS_PATTERN, False), (FOREACH_PATTERN, True)):
        for match in pattern.finditer(blanked):
            if resolve(match.group('lhs'), leading=is_element) is None:
                line = text.count('\n', 0, match.start()) + 1
                unresolved.append((line, match.group('lhs').strip()))

    for name, type_name in bindings.items():
        for match in re.finditer(rf'\b{name}((?:\s*\??\.\s*[A-Za-z_][A-Za-z0-9_]*)+)', blanked):
            segments = [s for s in re.sub(r'\s*\??\.\s*', '.', match.group(1)).split('.') if s]
            for owner, prop in walk(records, type_name, segments):
                reads.add((owner, prop))

    for line in unterminated:
        unresolved.append((line, 'a literal or block comment that never closes'))
    return bindings, reads, unresolved


def analyze(records, hasher_text):
    exclusions, unreasoned, loose = parse_exclusions(hasher_text)
    _bindings, reads, unresolved = parse_hasher(hasher_text, records)

    if loose != len(exclusions) + len(unreasoned):
        unresolved.append((0, f"{loose} spec-hash-excluded markers, {len(exclusions) + len(unreasoned)} parsed"))

    missing, hashed, unreadable, seen = [], [], [], set()
    queue = [ROOT_TYPE] if ROOT_TYPE in records else []
    while queue:
        type_name = queue.pop()
        if type_name in seen:
            continue
        seen.add(type_name)
        record = records[type_name]
        if record['positional']:
            unreadable.append(type_name)
            continue
        for prop, prop_type in sorted(record['props'].items()):
            key = (type_name, prop)
            if key in reads:
                hashed.append(key)
                leaf = unwrap(prop_type)
                if leaf in records:
                    queue.append(leaf)
            elif key in exclusions:
                continue
            else:
                missing.append(key)

    stale = []
    for (type_name, prop), (_reason, line) in sorted(exclusions.items()):
        record = records.get(type_name)
        if record is None:
            stale.append((line, type_name, prop, 'no such type'))
        elif prop not in record['props']:
            stale.append((line, type_name, prop, 'no such property'))
        elif (type_name, prop) in reads:
            stale.append((line, type_name, prop, 'the hasher DOES write it'))
        elif type_name not in seen:
            stale.append((line, type_name, prop, 'nothing hashed reaches this type'))

    return {
        'missing': sorted(missing), 'hashed': sorted(set(hashed)), 'exclusions': exclusions,
        'stale': stale, 'unresolved': unresolved, 'unreasoned': unreasoned,
        'unreadable': unreadable, 'types': sorted(seen),
    }


def _fixture(records, hasher):
    return analyze(parse_records(records)[0], hasher)


SELF_TEST_MISSING = {
    'a property nobody writes': (
        'public sealed record WorkloadDesiredSpec\n{\n    public required string Image { get; init; }\n'
        '    public string Forgotten { get; init; }\n}\n',
        'private static string Canonicalize(WorkloadDesiredSpec spec)\n{\n    w.WriteString("image", spec.Image);\n}\n',
        {('WorkloadDesiredSpec', 'Forgotten')}),

    'a property only a DOC COMMENT mentions': (
        'public sealed record WorkloadDesiredSpec\n{\n    public required string Image { get; init; }\n'
        '    public string Forgotten { get; init; }\n}\n',
        'private static string Canonicalize(WorkloadDesiredSpec spec)\n{\n'
        '    // Forgotten is deliberately not written here -- see spec.Forgotten.\n'
        '    w.WriteString("image", spec.Image);\n}\n',
        {('WorkloadDesiredSpec', 'Forgotten')}),

    'a property of a NESTED record': (
        'public sealed record WorkloadDesiredSpec\n{\n    public Nested? Inner { get; init; }\n}\n'
        'public sealed record Nested\n{\n    public required string A { get; init; }\n'
        '    public required string B { get; init; }\n}\n',
        'private static string Canonicalize(WorkloadDesiredSpec spec)\n{\n'
        '    if (spec.Inner is { } n)\n    {\n        w.WriteString("a", n.A);\n    }\n}\n',
        {('Nested', 'B')}),

    'a name that COLLIDES with a hashed property on another type': (
        'public sealed record WorkloadDesiredSpec\n{\n    public One? First { get; init; }\n'
        '    public Two? Second { get; init; }\n}\n'
        'public sealed record One\n{\n    public required string MountPath { get; init; }\n}\n'
        'public sealed record Two\n{\n    public required string MountPath { get; init; }\n}\n',
        'private static string Canonicalize(WorkloadDesiredSpec spec)\n{\n'
        '    if (spec.First is { } one)\n    {\n        w.WriteString("m", one.MountPath);\n    }\n'
        '    if (spec.Second is { } two)\n    {\n        w.WriteString("x", "y");\n    }\n}\n',
        {('Two', 'MountPath')}),

    'a property of a foreach ELEMENT': (
        'public sealed record WorkloadDesiredSpec\n{\n    public IReadOnlyList<Item> Items { get; init; } = [];\n}\n'
        'public sealed record Item\n{\n    public required string Id { get; init; }\n'
        '    public required string Size { get; init; }\n}\n',
        'private static string Canonicalize(WorkloadDesiredSpec spec)\n{\n'
        '    foreach (var i in spec.Items.OrderBy(x => x.Id, StringComparer.Ordinal))\n'
        '    {\n        w.WriteString("id", i.Id);\n    }\n}\n',
        {('Item', 'Size')}),

    'a property of a DICTIONARY VALUE': (
        'public sealed record WorkloadDesiredSpec\n{\n    public IReadOnlyDictionary<string, Item> Map { get; init; }\n}\n'
        'public sealed record Item\n{\n    public required string Id { get; init; }\n}\n',
        'private static string Canonicalize(WorkloadDesiredSpec spec)\n{\n    w.WriteNumber("n", spec.Map.Count);\n}\n',
        {('Item', 'Id')}),

    'an EXPRESSION-BODIED property nobody writes': (
        'public sealed record WorkloadDesiredSpec\n{\n    public required string Image { get; init; }\n'
        '    public string Derived => Image + "!";\n}\n',
        'private static string Canonicalize(WorkloadDesiredSpec spec)\n{\n    w.WriteString("image", spec.Image);\n}\n',
        {('WorkloadDesiredSpec', 'Derived')}),

    'a binding declared AFTER the one it depends on is read': (
        'public sealed record WorkloadDesiredSpec\n{\n    public Mid? Inner { get; init; }\n}\n'
        'public sealed record Mid\n{\n    public Deep? Deeper { get; init; }\n}\n'
        'public sealed record Deep\n{\n    public required string A { get; init; }\n'
        '    public required string B { get; init; }\n}\n',
        'private static string Canonicalize(WorkloadDesiredSpec spec)\n{\n'
        '    if (n.Deeper is { } d)\n    {\n        w.WriteString("a", d.A);\n    }\n'
        '    if (spec.Inner is { } n)\n    {\n        w.WriteString("x", "y");\n    }\n}\n',
        {('Deep', 'B')}),

    'a record DECLARED INSIDE another record body': (
        'public sealed record WorkloadDesiredSpec\n{\n    public required string Image { get; init; }\n'
        '    public Holder? Held { get; init; }\n}\n'
        'public sealed record Holder\n{\n    public required string Own { get; init; }\n'
        '    public sealed record Buried\n    {\n        public required string NotMine { get; init; }\n    }\n}\n',
        'private static string Canonicalize(WorkloadDesiredSpec spec)\n{\n'
        '    w.WriteString("image", spec.Image);\n'
        '    if (spec.Held is { } h)\n    {\n        w.WriteString("o", "1");\n    }\n}\n',
        {('Holder', 'Own')}),

    'a property read only through a HELPER that re-binds the type': (
        'public sealed record WorkloadDesiredSpec\n{\n    public Nested? Inner { get; init; }\n}\n'
        'public sealed record Nested\n{\n    public required string A { get; init; }\n'
        '    public required string B { get; init; }\n}\n',
        'private static string Canonicalize(WorkloadDesiredSpec spec)\n{\n'
        '    if (spec.Inner is { } n)\n    {\n        WriteNested(w, n);\n    }\n}\n'
        'private static void WriteNested(Utf8JsonWriter w, Nested nested)\n{\n'
        '    w.WriteString("a", nested.A);\n}\n',
        {('Nested', 'B')}),
}

SELF_TEST_CLEAN = {
    'a declared exclusion': (
        'public sealed record WorkloadDesiredSpec\n{\n    public required string Image { get; init; }\n'
        '    public string Nonce { get; init; }\n}\n',
        'private static string Canonicalize(WorkloadDesiredSpec spec)\n{\n'
        '    // spec-hash-excluded: WorkloadDesiredSpec.Nonce -- a restart nonce must never poison drift.\n'
        '    w.WriteString("image", spec.Image);\n}\n'),

    'an EXCLUDED property whose type nothing else reaches': (
        'public sealed record WorkloadDesiredSpec\n{\n    public required string Image { get; init; }\n'
        '    public IReadOnlyList<Note> Notes { get; init; } = [];\n}\n'
        'public sealed record Note\n{\n    public required string Text { get; init; }\n}\n',
        'private static string Canonicalize(WorkloadDesiredSpec spec)\n{\n'
        '    // spec-hash-excluded: WorkloadDesiredSpec.Notes -- carried informationally, never materialized.\n'
        '    w.WriteString("image", spec.Image);\n}\n'),

    'a chain that ends on a VALUE OBJECT outside the graph': (
        'public sealed record WorkloadDesiredSpec\n{\n    public required Health Health { get; init; }\n}\n'
        'public sealed record Health\n{\n    public required WorkloadHealthMode Mode { get; init; }\n}\n',
        'private static string Canonicalize(WorkloadDesiredSpec spec)\n{\n'
        '    w.WriteString("mode", spec.Health.Mode.Value);\n}\n'),

    'an EXPRESSION-BODIED property that is written': (
        'public sealed record WorkloadDesiredSpec\n{\n    public string Image => "x";\n}\n',
        'private static string Canonicalize(WorkloadDesiredSpec spec)\n{\n    w.WriteString("image", spec.Image);\n}\n'),
}

SELF_TEST_STALE = {
    'no such property': (
        'public sealed record WorkloadDesiredSpec\n{\n    public required string Image { get; init; }\n}\n',
        'private static string Canonicalize(WorkloadDesiredSpec spec)\n{\n'
        '    // spec-hash-excluded: WorkloadDesiredSpec.Gone -- removed two majors ago, entry never was.\n'
        '    w.WriteString("image", spec.Image);\n}\n'),

    'excluded AND written -- a contradiction, not redundancy': (
        'public sealed record WorkloadDesiredSpec\n{\n    public required string Image { get; init; }\n}\n',
        'private static string Canonicalize(WorkloadDesiredSpec spec)\n{\n'
        '    // spec-hash-excluded: WorkloadDesiredSpec.Image -- says one thing while the code does another.\n'
        '    w.WriteString("image", spec.Image);\n}\n'),

    'a type no hashed path reaches': (
        'public sealed record WorkloadDesiredSpec\n{\n    public required string Image { get; init; }\n}\n'
        'public sealed record Orphan\n{\n    public required string A { get; init; }\n}\n',
        'private static string Canonicalize(WorkloadDesiredSpec spec)\n{\n'
        '    // spec-hash-excluded: Orphan.A -- an entry for a record the hash cannot see from anywhere.\n'
        '    w.WriteString("image", spec.Image);\n}\n'),
}

SELF_TEST_UNREADABLE = {
    'a POSITIONAL record the graph reaches': (
        'public sealed record WorkloadDesiredSpec\n{\n    public Positional? Inner { get; init; }\n}\n'
        'public sealed record Positional(string A, string B);\n',
        'private static string Canonicalize(WorkloadDesiredSpec spec)\n{\n'
        '    if (spec.Inner is { } p)\n    {\n        w.WriteString("a", p.A);\n    }\n}\n'),
}

SELF_TEST_UNRESOLVED = {
    'a binding whose expression is not a spec path': (
        'public sealed record WorkloadDesiredSpec\n{\n    public required string Image { get; init; }\n}\n',
        'private static string Canonicalize(WorkloadDesiredSpec spec)\n{\n'
        '    w.WriteString("image", spec.Image);\n'
        '    if (SomewhereElse.Compute() is { } other)\n    {\n        w.WriteString("o", "1");\n    }\n}\n'),
}

SELF_TEST_UNREASONED = {
    'a marker with no real reason': (
        'public sealed record WorkloadDesiredSpec\n{\n    public required string Image { get; init; }\n'
        '    public string Nonce { get; init; }\n}\n',
        'private static string Canonicalize(WorkloadDesiredSpec spec)\n{\n'
        '    // spec-hash-excluded: WorkloadDesiredSpec.Nonce -- nope\n'
        '    w.WriteString("image", spec.Image);\n}\n'),
}


def self_test():
    failures = []
    for label, (records, hasher, wanted) in SELF_TEST_MISSING.items():
        result = _fixture(records, hasher)
        if set(result['missing']) != wanted:
            failures.append(f"MISSING  {label}: got {sorted(result['missing'])}, wanted {sorted(wanted)}")
        if result['unresolved']:
            failures.append(f"UNRESOLVED {label}: {result['unresolved']}")
    for label, (records, hasher) in SELF_TEST_CLEAN.items():
        result = _fixture(records, hasher)
        noise = result['missing'] + result['stale'] + result['unresolved'] + result['unreasoned']
        if noise:
            failures.append(f"FLAGGED  {label}: {noise}")
    for label, (records, hasher) in SELF_TEST_STALE.items():
        result = _fixture(records, hasher)
        if not result['stale']:
            failures.append(f"NOT STALE  {label}")
    for label, (records, hasher) in SELF_TEST_UNREADABLE.items():
        result = _fixture(records, hasher)
        if not result['unreadable']:
            failures.append(f"NOT UNREADABLE  {label}")
    for label, (records, hasher) in SELF_TEST_UNRESOLVED.items():
        result = _fixture(records, hasher)
        if not result['unresolved']:
            failures.append(f"NOT UNRESOLVED  {label}")
    for label, (records, hasher) in SELF_TEST_UNREASONED.items():
        result = _fixture(records, hasher)
        if not result['unreasoned']:
            failures.append(f"NOT UNREASONED  {label}")

    for failure in failures:
        print(f"  {failure}")
    total = (len(SELF_TEST_MISSING) + len(SELF_TEST_CLEAN) + len(SELF_TEST_STALE) + len(SELF_TEST_UNREADABLE)
             + len(SELF_TEST_UNRESOLVED) + len(SELF_TEST_UNREASONED))
    print(f"SELF-TEST {total - len(failures)}/{total}")
    return 1 if failures else 0


def cs_files(root):
    for base, _dirs, names in os.walk(os.path.join(ROOT, root)):
        if f'{os.sep}obj{os.sep}' in base + os.sep or f'{os.sep}bin{os.sep}' in base + os.sep:
            continue
        for name in sorted(names):
            if name.endswith('.cs'):
                yield os.path.join(base, name)


def main():
    check = '--check' in sys.argv[1:]
    if '--self-test' in sys.argv[1:]:
        return self_test()

    records, duplicates, unreadable_sources = {}, [], []
    for tree in RECORD_ROOTS:
        for path in cs_files(tree):
            with open(path, encoding='utf-8', errors='replace') as handle:
                parsed, unterminated = parse_records(handle.read())
                for line in unterminated:
                    unreadable_sources.append((os.path.relpath(path, ROOT), line))
                for name, record in parsed.items():
                    if name in records and records[name] != record:
                        duplicates.append(name)
                    records[name] = record

    hasher_path = os.path.join(ROOT, HASHER)
    with open(hasher_path, encoding='utf-8', errors='replace') as handle:
        hasher_text = handle.read()

    if f'Compute({ROOT_TYPE} ' not in hasher_text:
        print(f"FAIL: {HASHER} has no `Compute({ROOT_TYPE} ...)` -- ROOT_TYPE is stale.")
        return 1

    result = analyze(records, hasher_text)
    print(f"HASHED={len(result['hashed'])}  EXCLUDED={len(result['exclusions'])}  MISSING={len(result['missing'])}"
          f"  TYPES={len(result['types'])}  DECLARED RECORDS={len(records)}")
    print()
    for type_name in result['types']:
        hashed = [p for t, p in result['hashed'] if t == type_name]
        excluded = [p for t, p in result['exclusions'] if t == type_name]
        print(f"  {type_name:<28} hashed {len(hashed):>2}   excluded {len(excluded):>2}")

    if duplicates:
        print()
        for name in sorted(set(duplicates)):
            print(f"  FAIL: two different records are both called {name}; the graph cannot be walked by name.")
        return 1

    problems = False
    for path, line in unreadable_sources:
        print(f"\n  FAIL: {path}:{line}: a literal or block comment never closes.")
        print("  Over-blanking a record source deletes property declarations, and a property never seen is")
        print("  one this scan cannot report missing. This is the one direction it can fail quietly.")
        problems = True

    if result['unresolved']:
        print()
        for line, subject in result['unresolved']:
            print(f"  NOTE: {HASHER}:{line}: could not resolve `{subject}`.")
        print("  Not a failure: an unwalked binding loses READS, so it can only over-report. It is printed")
        print("  because a MISSING line below may be its shadow rather than a real hole.")

    for name in result['unreadable']:
        print(f"\n  FAIL: {name} is a POSITIONAL record; its properties are in a parameter list this scan")
        print("  does not read, so it would be graded as having none.")
        problems = True

    for line, subject, reason in result['unreasoned']:
        print(f"\n  FAIL: {HASHER}:{line}: the exclusion for {subject} gives no reason ({reason!r}).")
        print(f"  An exclusion is a decision; write down why, in at least {MIN_REASON} characters.")
        problems = True

    for line, type_name, prop, why in result['stale']:
        print(f"\n  FAIL: {HASHER}:{line}: the exclusion for {type_name}.{prop} is stale -- {why}.")
        problems = True

    if result['missing']:
        print()
        for type_name, prop in result['missing']:
            print(f"  {type_name}.{prop}")
        print()
        print("FAIL: each property above is reachable from the desired spec, is NOT written into the")
        print("canonical form, and is NOT declared excluded. Either canonicalize it, or add")
        print("`// spec-hash-excluded: Type.Property -- why` beside the code that decided not to.")
        print("Until then the engine cannot see drift on it, and nothing else will say so.")
        problems = True

    return 1 if (check and problems) else 0


if __name__ == '__main__':
    sys.exit(main())

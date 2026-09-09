"""Remplace la liste explicite de <Compile Include> des *.check.csproj par des globs.

Pourquoi : Unity genere une liste EXPLICITE de fichiers. Elle se perime des qu'un fichier
source est ajoute au projet — c'etait le cas le 2026-09-07 (les 6 MatchSessionManager_*.cs,
SupabaseDatabaseClient.cs, Core/DeterministicHash.cs, 6 fichiers de tests... n'etaient plus
compiles du tout par le compile-check, qui passait donc "au vert" sans les voir).
Un glob ne peut pas se perimer.
"""
import io
import re
import sys

HEADER = [
    '    <!-- Liste explicite remplacee par des globs (2026-09-07) : la liste generee par Unity',
    '         se perime des qu\'un fichier source est ajoute, et l\'etait — 6 fichiers serveur et 6',
    '         fichiers de test n\'etaient plus compiles du tout par ce compile-check, qui passait',
    '         donc au vert sans les voir. Un glob ne peut pas se perimer. -->',
]

GLOBS = {
    'runtime': [
        r'    <Compile Include="Assets\Scripts\**\*.cs" />',
        r'    <Compile Include="Assets\JMO Assets\WarFX\Scripts\**\*.cs" />',
        r'    <Compile Include="Assets\JMO Assets\WarFX\Spawn System\**\*.cs" />',
    ],
    'editor': [
        r'    <Compile Include="Assets\Editor\**\*.cs" />',
        r'    <Compile Include="Assets\JMO Assets\WarFX\Editor\**\*.cs" />',
    ],
}

COMPILE_RE = re.compile(r'^\s*<Compile Include=')


def globify(path, kind):
    with io.open(path, 'r', encoding='utf-8-sig', newline='') as f:
        lines = f.read().splitlines()

    out, done, in_block = [], False, False
    for line in lines:
        if COMPILE_RE.match(line):
            if not in_block:
                in_block = True
                if not done:
                    out.extend(HEADER)
                    out.extend(GLOBS[kind])
                    done = True
            continue  # on jette toutes les lignes <Compile Include> explicites
        in_block = False
        out.append(line)

    if not done:
        raise SystemExit('%s : aucune ligne <Compile Include> trouvee' % path)

    with io.open(path, 'w', encoding='utf-8', newline='\r\n') as f:
        f.write('\n'.join(out) + '\n')
    print('%s : liste explicite -> %d glob(s)' % (path, len(GLOBS[kind])))


if __name__ == '__main__':
    root = sys.argv[1]
    globify(root + r'\Assembly-CSharp.check.csproj', 'runtime')
    globify(root + r'\Assembly-CSharp-Server.check.csproj', 'runtime')
    globify(root + r'\Assembly-CSharp-Editor.check.csproj', 'editor')

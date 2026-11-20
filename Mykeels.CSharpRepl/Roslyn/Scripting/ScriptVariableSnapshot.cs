// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CSharpRepl.Services.Roslyn.Scripting;

/// <summary>One variable currently in scope in the persistent script state.</summary>
public sealed record ScriptVariableSnapshot(string Name, string Type, string? Value);

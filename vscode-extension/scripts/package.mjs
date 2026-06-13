// Builds a platform-specific, self-contained VSIX:
//   1. dotnet publish the CLI + MCP engine (self-contained, no .NET runtime needed) into bin/
//   2. compile the extension TS
//   3. vsce package --target <vscode-target>
//
// Usage: node scripts/package.mjs [vscode-target]   (defaults to this host's target)
//   e.g. node scripts/package.mjs win32-x64
import { execSync } from "node:child_process";
import { rmSync, mkdirSync, existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

// VS Code platform target  ->  .NET runtime identifier
const TARGETS = {
  "win32-x64": "win-x64",
  "win32-arm64": "win-arm64",
  "linux-x64": "linux-x64",
  "linux-arm64": "linux-arm64",
  "darwin-x64": "osx-x64",
  "darwin-arm64": "osx-arm64",
};

function hostTarget() {
  const os = process.platform === "win32" ? "win32" : process.platform === "darwin" ? "darwin" : "linux";
  const arch = process.arch === "arm64" ? "arm64" : "x64";
  return `${os}-${arch}`;
}

const here = dirname(fileURLToPath(import.meta.url));
const extRoot = join(here, "..");
const repoRoot = join(extRoot, "..");

const target = process.argv[2] || hostTarget();
const rid = TARGETS[target];
if (!rid) {
  console.error(`Unknown target "${target}". Valid: ${Object.keys(TARGETS).join(", ")}`);
  process.exit(1);
}
const platformTarget = rid.split("-")[1]; // x64 | arm64

const bin = join(extRoot, "bin");
const run = (cmd, cwd = extRoot) => execSync(cmd, { cwd, stdio: "inherit" });

console.log(`\n=== Bundling engine: target ${target} (rid ${rid}) ===`);
rmSync(bin, { recursive: true, force: true });
mkdirSync(bin, { recursive: true });

const publish = (proj) =>
  run(
    `dotnet publish "${join(repoRoot, proj)}" -c Release -f net10.0 -r ${rid} ` +
    `--self-contained true -p:PlatformTarget=${platformTarget} -o "${bin}"`
  );

// Both publish into the same bin/, sharing one copy of the .NET runtime.
publish("src/SsisLineage.Cli/SsisLineage.Cli.csproj");
publish("src/SsisLineage.Mcp/SsisLineage.Mcp.csproj");

if (!existsSync(join(bin, process.platform === "win32" ? "SsisLineage.Cli.exe" : "SsisLineage.Cli"))) {
  console.error("Engine publish did not produce the CLI apphost — aborting.");
  process.exit(1);
}

console.log("\n=== Compiling extension ===");
run("npm run compile");

console.log(`\n=== Packaging VSIX (${target}) ===`);
run(`npx --yes @vscode/vsce package --target ${target} -o "ssis-lineage-${target}.vsix"`);

console.log(`\nDone: ssis-lineage-${target}.vsix`);

import * as vscode from 'vscode';
import { spawn, ChildProcess } from 'child_process';
import * as readline from 'readline';

/**
 * Vegha VSCode extension. Spawns the CLI in --protocol json mode and keeps it
 * alive for the workspace lifetime — that way auth tokens, cookies, and connection
 * pools stay warm across multiple requests in a session.
 */
let cli: ChildProcess | undefined;
let pending = new Map<string, (response: any) => void>();
let nextId = 0;

function ensureCli(context: vscode.ExtensionContext): ChildProcess {
  if (cli && !cli.killed) return cli;

  const cliPath = vscode.workspace.getConfiguration('vegha').get<string>('cliPath') ?? 'vegha';
  cli = spawn(cliPath, ['--protocol', 'json'], { stdio: ['pipe', 'pipe', 'pipe'] });

  if (!cli.stdout || !cli.stdin) {
    throw new Error('Could not establish stdio with Vegha CLI');
  }

  const rl = readline.createInterface({ input: cli.stdout });
  rl.on('line', (line) => {
    try {
      const msg = JSON.parse(line);
      if (msg.id !== undefined) {
        const resolver = pending.get(String(msg.id));
        if (resolver) {
          pending.delete(String(msg.id));
          resolver(msg);
        }
      }
    } catch (e) {
      // CLI emitted non-JSON noise; ignore.
    }
  });

  cli.on('exit', () => {
    cli = undefined;
    pending.clear();
  });

  context.subscriptions.push({ dispose: () => cli?.kill() });
  return cli;
}

function rpc(context: vscode.ExtensionContext, method: string, params?: any): Promise<any> {
  const proc = ensureCli(context);
  return new Promise((resolve, reject) => {
    const id = String(++nextId);
    pending.set(id, (response) => {
      if (response.error) reject(new Error(response.error));
      else resolve(response.result);
    });
    proc.stdin?.write(JSON.stringify({ id, method, params }) + '\n');
  });
}

export function activate(context: vscode.ExtensionContext) {
  context.subscriptions.push(
    vscode.commands.registerCommand('vegha.ping', async () => {
      try {
        const r = await rpc(context, 'ping');
        vscode.window.showInformationMessage(`Vegha CLI: ${JSON.stringify(r)}`);
      } catch (e: any) {
        vscode.window.showErrorMessage(`Vegha: ${e.message}`);
      }
    }),

    vscode.commands.registerCommand('vegha.run', async () => {
      const editor = vscode.window.activeTextEditor;
      if (!editor) {
        vscode.window.showErrorMessage('No active editor');
        return;
      }
      let parsed: any;
      try {
        parsed = JSON.parse(editor.document.getText());
      } catch (e: any) {
        vscode.window.showErrorMessage(`Vegha: file is not JSON — ${e.message}`);
        return;
      }
      const result = await rpc(context, 'executeRequest', {
        method: parsed.method ?? 'GET',
        url: parsed.url,
        headers: parsed.headers ?? {},
        body: parsed.body,
        contentType: parsed.contentType,
      });

      const out = vscode.window.createOutputChannel('Vegha');
      out.appendLine(`${parsed.method ?? 'GET'} ${parsed.url}  →  ${result.status} ${result.statusText} (${result.elapsedMs} ms)`);
      out.appendLine(result.body ?? '');
      out.show(true);
    })
  );
}

export function deactivate() {
  cli?.kill();
}

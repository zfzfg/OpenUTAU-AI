import assert from 'node:assert/strict';
import { Client } from '@modelcontextprotocol/client';
import { StdioClientTransport } from '@modelcontextprotocol/client/stdio';
import { spawn } from 'node:child_process';
import { resolve,join } from 'node:path';
import { RpcPeer,stopOwnedProcess } from '../dist/test-library.js';
const directory=resolve(process.argv[2]||'artifacts/win-x64/AI');const executable=join(directory,process.platform==='win32'?'node.exe':'node');
const client=new Client({name:'clean-package-smoke',version:'1.0'},{capabilities:{}});
// The packaged process cannot resolve a development node_modules tree from cwd.
const env={...process.env,OPENUTAU_INSTANCE_ID:'',OPENUTAU_ACCESS_GRANT_REF:'',OPENUTAU_AUTOMATION_DIR:join(directory,'empty-private-discovery')};
await client.connect(new StdioClientTransport({command:executable,args:[join(directory,'mcp-server.js'),'--stdio'],cwd:directory,env,stderr:'pipe'}));
try{const tools=await client.listTools();assert.equal(tools.tools.length,27);}finally{await client.close();}
const helper=new Client({name:'clean-package-helper-smoke',version:'1.0'},{capabilities:{}});await helper.connect(new StdioClientTransport({command:executable,args:[join(directory,'assistant-tools.js'),'--stdio'],cwd:directory,env,stderr:'pipe'}));try{assert.equal((await helper.listTools()).tools.length,2);}finally{await helper.close();}
const child=spawn(executable,[join(directory,'agent-host.js'),'--stdio'],{cwd:directory,env,stdio:['pipe','pipe','pipe'],shell:false,windowsHide:true,detached:process.platform!=='win32'});child.stderr.on('data',()=>{});const peer=new RpcPeer(child.stdout,child.stdin);
try{const result=await peer.request('initialize',{apiVersion:'1',processGeneration:1});assert.equal(result.apiVersion,'1');assert.ok((await peer.request('providers.list',{})).providers.some(x=>x.id==='grok'));await peer.request('shutdown',{});}finally{await stopOwnedProcess(child);peer.close();}
process.stdout.write(JSON.stringify({ok:true,directory,tools:27,helperTools:2,host:true})+'\n');

import { mkdtemp, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { privateWrite } from '../dist/test-library.js';
export const fakeAgent=resolve('test/fixtures/fake-acp-agent.mjs');
export async function privateWorkspace(){
  const folder=await mkdtemp(join(tmpdir(),'OpenUtau AI Grüße '));
  if(process.platform==='win32'){
    const script="$ErrorActionPreference='Stop'; $p=$env:OPENUTAU_SECURITY_PATH; $u=[System.Security.Principal.WindowsIdentity]::GetCurrent().User; $a=[System.IO.Directory]::GetAccessControl($p); $a.SetAccessRuleProtection($true,$false); $a.SetAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($u,'FullControl','ContainerInherit,ObjectInherit','None','Allow')); [System.IO.Directory]::SetAccessControl($p,$a)";
    await promisify(execFile)('powershell.exe',['-NoProfile','-NonInteractive','-EncodedCommand',Buffer.from(script,'utf16le').toString('base64')],{windowsHide:true,env:{...process.env,OPENUTAU_SECURITY_PATH:folder}});
  }
  return folder;
}
export async function fakeEditor(options={}){
  const folder=await privateWorkspace();const requests=[];let projectSession='project_fixture';let healthChecks=0;let grantCounter=0;const instanceId=options.instanceId||'instance_fixture';const startedAt='2026-10-14T12:00:00.000Z';const token='descriptor_credential_0123456789';const credential='grant_credential_0123456789';const grants=new Map([[credential,'project_fixture']]);
  const server=createServer(async(req,res)=>{
    res.setHeader('content-type','application/json');
    if(req.url==='/automation/v1/health'){healthChecks++;res.end(JSON.stringify({instanceId,startedAt,apiVersion:'1',ready:true}));return;}
    let text='';for await(const chunk of req)text+=chunk;const body=JSON.parse(text||'{}');requests.push({url:req.url,body,authorization:req.headers.authorization});
    if(req.url==='/automation/v1/connect'){
      if(req.headers.authorization!==`Bearer ${token}`){res.writeHead(401);res.end(JSON.stringify({ok:false,error:{code:'UNAUTHORIZED',message:'Invalid token.'}}));return;}
      if(body.projectSessionId&&body.projectSessionId!==projectSession){res.writeHead(409);res.end(JSON.stringify({ok:false,error:{code:'PROJECT_CHANGED',message:'Project changed.'}}));return;}
      const connectionCredential=`connection_credential_${++grantCounter}`;grants.set(connectionCredential,projectSession);
      res.end(JSON.stringify({credential:connectionCredential,grantId:'grant_fixture',projectSessionId:projectSession,mode:'apply'}));return;
    }
    const bound=grants.get((req.headers.authorization||'').replace(/^Bearer /,''));
    if(!bound){res.writeHead(401);res.end(JSON.stringify({ok:false,error:{code:'UNAUTHORIZED',message:'Invalid token.'}}));return;}
    if(bound!==projectSession||body.arguments?.projectSessionId&&body.arguments.projectSessionId!==projectSession){res.end(JSON.stringify({ok:false,error:{code:'PROJECT_CHANGED',message:'Project changed.'}}));return;}
    res.end(JSON.stringify({ok:true,projectSessionId:projectSession,editRevision:7,operation:body.operation,...(body.arguments?.operationId?{operationId:body.arguments.operationId,status:'committed'}:{})}));
  });server.listen(0,'127.0.0.1');await once(server,'listening');const url=`http://127.0.0.1:${server.address().port}`;
  await privateWrite(join(folder,`${instanceId}.json`),{instanceId,pid:process.pid,startedAt,apiVersion:'1',url,token,appVersion:'fixture'});
  const grantRef=join(folder,'grant.native');await privateWrite(grantRef,{url,instanceId,credential,projectSessionId:'project_fixture',processGeneration:1,mode:'apply'});
  return {folder,server,url,grantRef,requests,instanceId,get healthChecks(){return healthChecks;},setProjectSession(value){projectSession=value;},async dispose(){server.closeAllConnections();await new Promise(resolve=>server.close(resolve));await rm(folder,{recursive:true,force:true});}};
}
export async function until(predicate,timeout=5000){const until=Date.now()+timeout;while(Date.now()<until){const value=predicate();if(value)return value;await new Promise(resolve=>setTimeout(resolve,10));}throw Error('Expected asynchronous event did not arrive.');}

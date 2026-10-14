import { readFile,writeFile,mkdir,cp,copyFile,chmod,stat,readdir } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { resolve,join,basename,relative } from 'node:path';
import { pipeline } from 'node:stream/promises';
import { createWriteStream } from 'node:fs';
import { Readable } from 'node:stream';
const args=process.argv.slice(2);const value=name=>args.includes(name)?args[args.indexOf(name)+1]:undefined;
const lock=JSON.parse(await readFile('scripts/node-runtime-lock.json','utf8'));
const rid=value('--rid')||`${process.platform==='win32'?'win':process.platform==='darwin'?'osx':'linux'}-${process.arch}`;
const runtime=lock.runtimes[rid];if(!runtime)throw Error(`AI runtime is unsupported for RID ${rid}; the desktop app can still run without AI.`);
const destination=resolve(value('--out')||join('artifacts',rid,'AI'));await mkdir(destination,{recursive:true});
const cache=resolve('.runtime-cache');await mkdir(cache,{recursive:true});const archive=join(cache,runtime.archive);
try{await stat(archive);}catch{
  const response=await fetch(`https://nodejs.org/dist/v${lock.version}/${runtime.archive}`,{signal:AbortSignal.timeout(60000)});if(!response.ok||!response.body)throw Error('Node runtime download failed.');
  await pipeline(Readable.fromWeb(response.body),createWriteStream(archive,{flags:'wx'}));
}
const archiveHash=createHash('sha256').update(await readFile(archive)).digest('hex');if(archiveHash!==runtime.sha256)throw Error('Pinned Node archive checksum mismatch. Delete the corrupt cache file and retry.');
const unpacked=join(cache,`unpacked-${rid}`);await mkdir(unpacked,{recursive:true});
const extractor=runtime.archive.endsWith('.zip')&&process.platform!=='win32'?'unzip':'tar';const extractionArgs=extractor==='unzip'?['-o','-q',archive,'-d',unpacked]:['-xf',archive,'-C',unpacked];
const child=spawn(extractor,extractionArgs,{shell:false,windowsHide:true,stdio:'inherit'});const [code]=await once(child,'exit');if(code!==0)throw Error('Node archive extraction failed.');
const prefix=runtime.archive.replace(/\.(zip|tar\.gz)$/,'');const source=join(unpacked,prefix);const executable=rid.startsWith('win-')?'node.exe':'node';
await copyFile(join(source,runtime.binary),join(destination,executable));await copyFile(join(source,'LICENSE'),join(destination,'NODE_LICENSE.txt'));if(!rid.startsWith('win-'))await chmod(join(destination,executable),0o755);
for(const name of ['mcp-server.js','agent-host.js','assistant-tools.js','package.json'])await copyFile(join('dist',name),join(destination,name));
for(const name of (await readdir('dist')).filter(x=>x.endsWith('.LEGAL.txt')&&!x.startsWith('test-')))await copyFile(join('dist',name),join(destination,name));
await cp('dist/contracts',join(destination,'contracts'),{recursive:true});await mkdir(join(destination,'licenses'),{recursive:true});
const productionPackages=['@modelcontextprotocol/server','@modelcontextprotocol/core','zod','ajv','fast-deep-equal','fast-uri','json-schema-traverse','require-from-string'];
const licenseInventory=[];
for(const name of productionPackages){const packagePath=join('node_modules',name);const manifest=JSON.parse(await readFile(join(packagePath,'package.json'),'utf8'));const files=(await readdir(packagePath)).filter(x=>/^(LICENSE|COPYING|NOTICE)(\.|$|-)/i.test(x));if(!files.length)throw Error(`Missing license for ${name}`);for(const file of files)await copyFile(join(packagePath,file),join(destination,'licenses',`${name.replaceAll('/','_')}_${file}`));licenseInventory.push({name,version:manifest.version,license:manifest.license});}
const manifest=JSON.parse(await readFile('dist/manifest.json','utf8'));manifest.runtime={rid,version:lock.version,archive:runtime.archive,archiveSha256:archiveHash,executable,sha256:createHash('sha256').update(await readFile(join(destination,executable))).digest('hex')};manifest.productionDependencies=licenseInventory;manifest.optionalAdapters=lock.optionalAdapters;
await writeFile(join(destination,'manifest.json'),JSON.stringify(manifest,null,2)+'\n');await writeFile(join(destination,'THIRD_PARTY_NOTICES.md'),'# Bundled third-party software\n\nNode.js '+lock.version+' (MIT and included component notices): NODE_LICENSE.txt.\n\n'+licenseInventory.map(x=>`- ${x.name} ${x.version}: ${x.license}; see licenses/.`).join('\n')+'\n\nProvider CLIs and optional ACP adapters are not bundled. Their own authentication, subscriptions, licenses and native filesystem/shell permissions apply.\n');
process.stdout.write(JSON.stringify({rid,destination,nodeVersion:lock.version,archiveSha256:archiveHash})+'\n');

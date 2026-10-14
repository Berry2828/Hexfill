import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { Workbook, SpreadsheetFile } from '@oai/artifact-tool';

const builderDir = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(builderDir, '..', '..');
const args = process.argv.slice(2);
function option(name) { const i = args.indexOf(name); return i < 0 ? undefined : args[i + 1]; }
const notes = JSON.parse(await fs.readFile(path.join(builderDir, 'release-notes.json'), 'utf8'));
const version = option('--version') ?? notes.version;
if (version !== notes.version || !notes.previousVersion || !notes.changes?.length || !notes.changedCommands?.length)
  throw new Error('Manual release notes are missing or do not match DLL version');
const date = option('--date') ?? new Intl.DateTimeFormat('en-CA', {timeZone:'Asia/Shanghai',year:'numeric',month:'2-digit',day:'2-digit'}).format(new Date()).replaceAll('-', '');
if (!/^\d{8}$/.test(date)) throw new Error('Manual date must be yyyyMMdd');
const displayDate = `${date.slice(0,4)}-${date.slice(4,6)}-${date.slice(6,8)}`;
const out = path.resolve(option('--out-dir') ?? path.join(root, 'outputs', `hexfill-manual-${date}`));
const dllName = option('--dll-name') ?? `HexFill_${date}_Full.dll`;
const target = path.join(out, `HexFill_${version}_命令使用说明书_${date}.xlsx`);
await fs.mkdir(out, {recursive:true});
const base = JSON.parse(await fs.readFile(path.join(builderDir, 'command-manual.json'), 'utf8'));
const {commands, flow, csv} = base;
for (const row of commands) {
  const update = notes.commandUpdates?.[row[0]];
  if (update?.purpose) row[2] = update.purpose;
  if (update?.steps) row[4] = update.steps;
  if (update?.notes) row[5] = update.notes;
}
for (const row of flow) { const update = notes.workflowUpdates?.[row[0]]; if (update) { row[1]=update[0]; row[2]=update[1]; } }
for (const row of csv) { const update = notes.csvUpdates?.[row[0]]; if (update) { row[2]=update[0]; row[3]=update[1]; } }
const changed = new Set(notes.changes.map(row=>row[0]));
if (notes.changedCommands.some(name=>!changed.has(name) || !notes.commandUpdates?.[name]) || changed.size!==notes.changedCommands.length)
  throw new Error('Every changed command needs release notes and current usage documentation');
const wb = Workbook.create();
const font = 'Microsoft YaHei';
function estimatedHeight(values, widths, min=80) {
  let lines = 1;
  values.forEach((v,i) => {
    const count = String(v??'').split('\n').reduce((n,t) => n+Math.max(1,Math.ceil([...t].reduce((w,c)=>w+(c.charCodeAt(0)>255?14.5:7.5),0)/(widths[i]-18))),0);
    lines = Math.max(lines,count);
  });
  return Math.max(min,lines*21+20);
}

function tableSheet(name,title,context,headers,rows,widths,tableName,minHeight) {
  const sheet=wb.worksheets.add(name);
  sheet.showGridLines=false;
  sheet.tabColor='#284B63';
  const end=String.fromCharCode(64+headers.length);
  sheet.getRange(`A1:${end}${rows.length+6}`).format.font={name:font,size:11,color:'#263445'};
  sheet.getRange('A2').values=[[title]];
  sheet.getRange('A2').format.font={name:font,size:16,bold:true,color:'#173B56'};
  sheet.getRange('A2').format.wrapText=false;
  sheet.getRange(`A2:${end}2`).format.rowHeightPx=36;
  sheet.getRange('A3').values=[[context]];
  sheet.getRange('A3').format.font={name:font,size:11,color:'#5B6A76'};
  sheet.getRange(`A4:${end}4`).format.borders={bottom:{style:'thin',color:'#284B63'}};
  sheet.getRange(`A5:${end}${rows.length+5}`).values=[headers,...rows];
  const table=sheet.tables.add(`A5:${end}${rows.length+5}`,true,tableName);
  table.showFilterButton=true;
  sheet.getRange(`A5:${end}5`).format={fill:'#284B63',font:{name:font,size:11,bold:true,color:'#FFFFFF'},horizontalAlignment:'center',verticalAlignment:'center',rowHeightPx:36};
  const body=sheet.getRange(`A6:${end}${rows.length+5}`);
  body.format.wrapText=true;
  body.format.verticalAlignment='top';
  body.format.horizontalAlignment='left';
  widths.forEach((width,i)=>sheet.getRange(`${String.fromCharCode(65+i)}1:${String.fromCharCode(65+i)}${rows.length+6}`).format.columnWidthPx=width);
  rows.forEach((row,i)=>{
    const range=sheet.getRange(`A${i+6}:${end}${i+6}`);
    range.format.rowHeightPx=estimatedHeight(row,widths,minHeight);
    range.format.fill=i%2===0?'#F1F5F8':'#FFFFFF';
  });
  sheet.freezePanes.freezeRows(5);
  return sheet;
}

const changeSheet=tableSheet('本版改动','本版修改命令、功能变化与问题修复',`版本 ${notes.previousVersion} 至 ${version}　　发布日期 ${displayDate}`,['修改命令','关联命令','修复的问题','功能有什么变化','参数与使用影响'],notes.changes,[110,170,400,460,460],'HexFillReleaseChanges',110);
changeSheet.getRange('A12').values=[[`本版实际修改：${notes.changedCommands.join('、')}。HBR、HEX 作为关联导出命令，导出字段保持。`]];
changeSheet.getRange('A14').values=[[`配套 DLL：${dllName}　　文件版本：${version}`]];
changeSheet.getRange('A12:E14').format.font={name:font,size:11,color:'#5B6A76'};
changeSheet.getRange('A12:E14').format.rowHeightPx=26;
const mainRows=commands.map((c,i)=>[i+1,...c]);
const main=tableSheet('命令说明','HexFill 命令使用说明书',`版本 ${version}　　${commands.length} 个命令　　更新日期 ${displayDate}`,['序号','命令','名称','功能','适用对象','操作步骤','参数与注意'],mainRows,[50,85,175,280,220,440,450],'HexFillCommands',96);
main.getRange('A6:A24').format.horizontalAlignment='center';
main.getRange('B6:C24').format.font={name:font,size:11,bold:true,color:'#173B56'};
main.freezePanes.freezeColumns(3);
tableSheet('上手与流程','加载与常用操作流程','从加载插件到铺点、编号、数据往返和问题处理',['主题','操作方法','说明'],flow,[215,665,560],'HexFillWorkflows',80);
tableSheet('CSV与参数','CSV 数据与参数说明','数据往返规则、默认规格和处理边界',['项目','适用命令','字段或规则','填写与处理说明'],csv,[170,260,560,510],'HexFillCsvRules',72);

// Verify the authored list against the actual registered command set.
const sources=[`${root}/HexFill.cs`,...(await fs.readdir(`${root}/Commands`)).filter(n=>n.endsWith('.cs')).map(n=>`${root}/Commands/${n}`)];
const registered=new Set();
for(const file of sources) for(const match of (await fs.readFile(file,'utf8')).matchAll(/\[CommandMethod\("([A-Z]+)"\)\]/g)) registered.add(match[1]);
if(registered.size!==commands.length || commands.some(c=>!registered.has(c[0]))) throw new Error('Command list does not match release source');
wb.recalculate();
console.log((await wb.inspect({kind:'table',range:'命令说明!A5:C24',include:'values',tableMaxRows:20,tableMaxCols:3,maxChars:1600})).ndjson);
console.log((await wb.inspect({kind:'match',searchTerm:'#REF!|#DIV/0!|#VALUE!|#NAME\\?|#NUM!',options:{useRegex:true,maxResults:20},summary:'Final error scan',maxChars:600})).ndjson);
for(const [sheetName,range,label] of [['本版改动','A1:E14','release-changes'],['命令说明','A1:G9','commands-top'],['命令说明','A16:G18','commands-brightness'],['命令说明','A19:G22','commands-geometry'],['命令说明','A22:G24','commands-bottom'],['上手与流程','A1:C11','workflow'],['上手与流程',`A${flow.length+5}:C${flow.length+5}`,'workflow-validation'],['CSV与参数','A1:D17','csv']]) {
  const preview=await wb.render({sheetName,range,scale:1,format:'png'});
  await fs.writeFile(path.join(out,`${label}.png`),new Uint8Array(await preview.arrayBuffer()));
}
const output=await SpreadsheetFile.exportXlsx(wb);
await output.save(target);
await fs.writeFile(path.join(out,'manual-content.json'),JSON.stringify({commands,flow,csv,changes:notes.changes},null,2),'utf8');
await fs.writeFile(path.join(out,'manual-manifest.json'),JSON.stringify({target,version,previousVersion:notes.previousVersion,date,dllName,commands:commands.map(row=>row[0]),changedCommands:notes.changedCommands,sheets:['本版改动','命令说明','上手与流程','CSV与参数']},null,2),'utf8');
console.log(JSON.stringify({target,version,commands:commands.length,changedCommands:notes.changedCommands,sheets:4}));

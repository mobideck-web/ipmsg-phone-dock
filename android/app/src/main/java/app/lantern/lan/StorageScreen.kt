package app.lantern.lan

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.Alignment
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

@Composable fun StorageScreen(model:Model,onBack:()->Unit){
    val scope=rememberCoroutineScope();var snapshot by remember{mutableStateOf<StorageSnapshot?>(null)}
    var selected by remember{mutableStateOf(setOf<String>())};var refresh by remember{mutableIntStateOf(0)}
    var busy by remember{mutableStateOf(false)};var confirm by remember{mutableStateOf(false)};var result by remember{mutableStateOf<String?>(null)}
    BackHandler(onBack=onBack)
    LaunchedEffect(refresh){snapshot=withContext(Dispatchers.IO){StorageManager.scan(model)};selected=emptySet()}
    Column(Modifier.fillMaxSize(),verticalArrangement=Arrangement.spacedBy(12.dp)){
        Row(verticalAlignment=Alignment.CenterVertically){TextButton(onClick=onBack){Text("‹ Back")};Text("Storage",style=MaterialTheme.typography.headlineMedium)}
        Text("Remove files without clearing your conversations.",style=MaterialTheme.typography.bodyMedium,color=MaterialTheme.colorScheme.onSurfaceVariant)
        val data=snapshot
        if(data==null)LinearProgressIndicator(Modifier.fillMaxWidth()) else {
            Card(Modifier.fillMaxWidth()){Column(Modifier.padding(20.dp),verticalArrangement=Arrangement.spacedBy(6.dp)){
                Text(fileSize(data.items.filterNot{it.temporary}.sumOf{it.bytes}),style=MaterialTheme.typography.headlineMedium)
                Text("Downloaded files · ${fileSize(data.free)} free")
                Text("${fileSize(data.items.filter{it.temporary}.sumOf{it.bytes})} unused temporary copies",style=MaterialTheme.typography.bodySmall)
                if(data.keptTemporary>0)Text("${fileSize(data.keptTemporary)} temporary files retained for queued or linked items",style=MaterialTheme.typography.bodySmall)
            }}
            data.warning?.let{Text(it,color=MaterialTheme.colorScheme.error)}
            result?.let{Text(it,style=MaterialTheme.typography.bodyMedium)}
            Row{TextButton(enabled=!busy,onClick={selected=if(selected.size==data.items.size)emptySet()else data.items.map{it.uri}.toSet()}){Text(if(selected.size==data.items.size && selected.isNotEmpty())"Deselect all" else "Select all")};Spacer(Modifier.weight(1f));TextButton(enabled=!busy,onClick={refresh++}){Text("Refresh")}}
            LazyColumn(Modifier.weight(1f),verticalArrangement=Arrangement.spacedBy(8.dp)){
                if(data.items.isEmpty())item{Text("No removable files. Active transfers and queued files are protected.",Modifier.padding(vertical=20.dp))}
                items(data.items,key={it.uri}){item->Card(Modifier.fillMaxWidth()){Row(Modifier.padding(8.dp),verticalAlignment=Alignment.CenterVertically){
                    Checkbox(modifier=Modifier.semantics{contentDescription="Select ${item.name}"},checked=item.uri in selected,onCheckedChange={checked->selected=if(checked)selected+item.uri else selected-item.uri},enabled=!busy)
                    Column(Modifier.weight(1f)){Text(item.name,maxLines=2);Text(fileSize(item.bytes)+if(item.temporary)" · Temporary" else " · Download",style=MaterialTheme.typography.bodySmall,color=MaterialTheme.colorScheme.onSurfaceVariant)}
                }}}
            }
            Button(enabled=selected.isNotEmpty()&&!busy,onClick={confirm=true},modifier=Modifier.fillMaxWidth()){Text(if(busy)"Removing…" else "Remove selected (${selected.size})")}
            Text("Temporary copies must be unused and older than one hour. Saved recordings and files outside IPMsg downloads are excluded.",style=MaterialTheme.typography.bodySmall,color=MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
    if(confirm)AlertDialog(onDismissRequest={confirm=false},title={Text("Remove selected files?")},text={Text("Delete ${selected.size} files (${fileSize(snapshot?.items.orEmpty().filter{it.uri in selected}.sumOf{it.bytes})}) from storage? This cannot be undone. Chat messages remain.")},confirmButton={TextButton(onClick={confirm=false;busy=true;val chosen=snapshot?.items.orEmpty().filter{it.uri in selected};scope.launch{val counts=withContext(Dispatchers.IO){StorageManager.remove(model,chosen)};result="${counts.first} removed"+if(counts.second>0)" · ${counts.second} could not be removed. Check access or active transfers." else "";busy=false;refresh++}}){Text("Remove files")}},dismissButton={TextButton(onClick={confirm=false}){Text("Cancel")}})
}

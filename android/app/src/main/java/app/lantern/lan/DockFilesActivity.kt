package app.lantern.lan

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.graphics.BitmapFactory
import android.net.Uri
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectDragGesturesAfterLongPress
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.util.UUID

/** Owner-controlled file shelf; this component never grants access from an intent alone. */
open class DockFilesActivity:ComponentActivity() {
    private var pendingToken by mutableStateOf<String?>(null)
    private var shareNotice by mutableStateOf("")
    private var showPhotos by mutableStateOf(false)
    private val owner="picker-${UUID.randomUUID()}"
    private val documents=registerForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) {uris-> add(uris) }
    private val photos=registerForActivityResult(ActivityResultContracts.PickMultipleVisualMedia(30)) {uris->add(uris)}
    private val folders=registerForActivityResult(ActivityResultContracts.OpenDocumentTree()) {uri->
        if(uri!=null) {shareNotice="";showPhotos=false;DockSessions.importFolder(this,uri,owner)}
    }
    private val notifications=registerForActivityResult(ActivityResultContracts.RequestPermission()) { }
    override fun onCreate(savedInstanceState:Bundle?) {
        super.onCreate(savedInstanceState); consume(intent)
        setContent {
            val state by DockSessions.state.collectAsState()
            IPMsgTheme(isSystemInDarkTheme()) {
                Surface(Modifier.fillMaxSize()) {
                    Column(Modifier.fillMaxSize().systemBarsPadding().padding(horizontal=20.dp,vertical=16.dp),verticalArrangement=Arrangement.spacedBy(14.dp)) {
                        Row(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.SpaceBetween) {
                            Text("Phone Dock",style=MaterialTheme.typography.headlineSmall)
                            if(state.active) TextButton(onClick={DockSessions.stop()}) {Text("Stop")}
                        }
                        Text(if(state.active) "Your selected files, on your PC." else "Connect Phone Dock on your PC to start.",style=MaterialTheme.typography.bodyLarge)
                        if(state.active) {
                            Row(horizontalArrangement=Arrangement.spacedBy(10.dp)) {
                                Button(onClick={showPhotos=false;documents.launch(arrayOf("*/*"))},enabled=!state.preparing) {Text("Files")}
                                OutlinedButton(onClick={showPhotos=true;photos.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageAndVideo))},enabled=!state.preparing) {Text("Photos")}
                                OutlinedButton(onClick={folders.launch(null)},enabled=!state.preparing) {Text("Folder")}
                            }
                            Row(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
                                FilterChip(selected=!showPhotos,onClick={showPhotos=false},label={Text("Files")})
                                FilterChip(selected=showPhotos,onClick={showPhotos=true},label={Text("Photos & videos")})
                                TextButton(onClick={DockSessions.edge(this@DockFilesActivity)}) {Text("Drop edge")}
                            }
                        }
                        if(state.preparing) LinearProgressIndicator(Modifier.fillMaxWidth())
                        Text(shareNotice.ifBlank{state.message},style=MaterialTheme.typography.bodySmall,color=MaterialTheme.colorScheme.onSurfaceVariant)
                        val visible=state.items.filter{!showPhotos || it.mime.startsWith("image/") || it.mime.startsWith("video/")}
                        LazyVerticalGrid(GridCells.Adaptive(142.dp),Modifier.weight(1f),horizontalArrangement=Arrangement.spacedBy(12.dp),verticalArrangement=Arrangement.spacedBy(12.dp)) {
                            items(visible,key={it.id}) {item-> FileCard(item)}
                        }
                        Text("Hold a card, then move the PC pointer out to its shelf. Wait for Ready before dragging to Windows. Screen lock or Stop ends access.",style=MaterialTheme.typography.bodySmall,color=MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    pendingToken?.let {candidate ->
                        AlertDialog(onDismissRequest={pendingToken=null},title={Text("Connect Phone Dock?")},
                            text={Text("Allow this PC session to receive only files, photos and folders you choose here or share to IPMsg Dock? Folders include their contents. It lasts up to one hour and stops when your phone locks. Replacing a session ends the old PC's access.")},
                            confirmButton={TextButton(onClick={
                                pendingToken=null
                                if(android.os.Build.VERSION.SDK_INT>=33) notifications.launch(Manifest.permission.POST_NOTIFICATIONS)
                                ContextCompat.startForegroundService(this@DockFilesActivity,Intent(this@DockFilesActivity,DockBridgeService::class.java).putExtra("dock_session",candidate))
                            }){Text("Allow session")}},dismissButton={TextButton(onClick={pendingToken=null}){Text("Cancel")}})
                    }
                }
            }
        }
    }
    override fun onNewIntent(intent:Intent) {super.onNewIntent(intent);setIntent(intent);consume(intent)}
    private fun consume(incoming:Intent) {
        val requested=DockPolicy.ownerRequest(this is DockShareActivity,runCatching{incoming.getStringExtra("dock_session")}.getOrNull())
        incoming.removeExtra("dock_session")
        if(requested!=null) {
            if(!DockPolicy.token(requested)) {DockSessions.notice("Invalid PC connection request. Reconnect from Phone Dock.");return}
            if(!DockSessions.matches(requested)) {
                if(pendingToken==null || pendingToken==requested) pendingToken=requested
                else DockSessions.notice("Finish the current connection request before opening another.")
            }
        }
        if(incoming.action==Intent.ACTION_SEND || incoming.action==Intent.ACTION_SEND_MULTIPLE) {
            @Suppress("DEPRECATION") val uris=runCatching {
                when(incoming.action) {
                    Intent.ACTION_SEND -> listOfNotNull(incoming.getParcelableExtra<Uri>(Intent.EXTRA_STREAM))
                    else -> incoming.getParcelableArrayListExtra<Uri>(Intent.EXTRA_STREAM).orEmpty()
                }.ifEmpty { incoming.clipData?.let{clip->(0 until minOf(clip.itemCount,31)).mapNotNull{clip.getItemAt(it).uri}}.orEmpty() }
            }.getOrDefault(emptyList())
            val text=incoming.getCharSequenceExtra(Intent.EXTRA_TEXT)?.toString()?.take(1000)
            if(uris.isEmpty()) shareNotice=if(text.isNullOrBlank()) "Share an actual file or photo from its app." else "Shared text/link: $text\nThis is a link, not the original file."
            else if(incoming.flags and Intent.FLAG_GRANT_READ_URI_PERMISSION==0) shareNotice="The source did not grant file access. Use its Share button again."
            else if(!DockSessions.active()) shareNotice="Connect Phone Dock on your PC, then share the files again."
            else add(uris)
            incoming.action=null;incoming.removeExtra(Intent.EXTRA_STREAM);incoming.removeExtra(Intent.EXTRA_TEXT)
        }
    }
    private fun add(uris:List<Uri>) {if(uris.isNotEmpty()) {shareNotice="";DockSessions.import(this,uris,owner)}}
    override fun onStop() {DockSessions.end(true);super.onStop()}

    @Composable private fun FileCard(item:DockExport) {
        val folder=item.mime==DockFolderArchive.MIME
        val displayName=if(folder) item.name.removeSuffix(".zip") else item.name
        var preview by remember(item.id){mutableStateOf(false)}
        val bitmap by produceState<android.graphics.Bitmap?>(null,item.thumbnail) {
            value=withContext(Dispatchers.IO){item.thumbnail?.let{BitmapFactory.decodeByteArray(it,0,it.size)}}
        }
        val shape=RoundedCornerShape(18.dp)
        Column(Modifier.background(MaterialTheme.colorScheme.surfaceContainer,shape)
            .pointerInput(item.id) {detectDragGesturesAfterLongPress(onDragStart={DockSessions.begin(listOf(item.id))},
                onDragEnd={DockSessions.end()},onDragCancel={DockSessions.end(true)},onDrag={change,_->change.consume()})}
            .clickable{preview=true}.padding(12.dp),verticalArrangement=Arrangement.spacedBy(8.dp)) {
            bitmap?.let{Image(it.asImageBitmap(),item.name,Modifier.fillMaxWidth().height(110.dp),contentScale=ContentScale.Crop)}
                ?: Text(if(folder) "FOLDER" else if(item.mime.startsWith("video/")) "VIDEO" else "FILE",Modifier.fillMaxWidth().height(72.dp),style=MaterialTheme.typography.labelLarge)
            Text(displayName,maxLines=2,style=MaterialTheme.typography.titleSmall)
            Text(size(item.size),style=MaterialTheme.typography.bodySmall,color=MaterialTheme.colorScheme.onSurfaceVariant)
        }
        if(preview) AlertDialog(onDismissRequest={preview=false},title={Text(displayName,maxLines=3)},
            text={Column(verticalArrangement=Arrangement.spacedBy(12.dp)){bitmap?.let{Image(it.asImageBitmap(),item.name,Modifier.fillMaxWidth().heightIn(max=340.dp),contentScale=ContentScale.Fit)};Text("${if(folder) "Folder and its contents" else item.mime}\n${size(item.size)}\nHold the card to hand it to your PC.")}},
            confirmButton={TextButton(onClick={preview=false}){Text("Done")}})
    }
    private fun size(bytes:Long)=when {bytes>=1024L*1024*1024 -> "%.1f GB".format(bytes/(1024.0*1024*1024));bytes>=1024*1024->"%.1f MB".format(bytes/(1024.0*1024));bytes>=1024->"%.0f KB".format(bytes/1024.0);else->"$bytes B"}
}

/** Dedicated Android Sharesheet target, separate from IPMsg's chat sharing flow. */
class DockShareActivity:DockFilesActivity()

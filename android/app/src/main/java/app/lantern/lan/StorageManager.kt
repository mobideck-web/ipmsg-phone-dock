package app.lantern.lan

import android.content.ContentUris
import android.content.ContentValues
import android.net.Uri
import android.provider.MediaStore
import android.provider.DocumentsContract
import java.io.File

data class StorageItem(val uri:String,val name:String,val bytes:Long,val temporary:Boolean=false,val local:File?=null)
data class StorageSnapshot(val items:List<StorageItem>,val free:Long,val keptTemporary:Long,val warning:String?=null)
object StorageManager {
    private fun protected(model:Model):Set<String> = model.repository.outbox().flatMap{it.uris}.toSet() + model.engine?.retainedFileUris().orEmpty() + model.state.value.transfers.filter{it.active}.flatMap{listOfNotNull(it.savedUri,it.sourceUri)}
    private fun linked(model:Model):Set<String> = model.history.readableDatabase.rawQuery("SELECT sourceUri FROM files WHERE sourceUri IS NOT NULL",null).use{c->buildSet{while(c.moveToNext())add(c.getString(0))}}
    fun scan(model:Model):StorageSnapshot {
        val result=linkedMapOf<String,StorageItem>();val resolver=model.context.contentResolver;var warning:String?=null
        val protect=protected(model)
        runCatching {
            resolver.query(MediaStore.Downloads.EXTERNAL_CONTENT_URI,arrayOf(MediaStore.MediaColumns._ID,MediaStore.MediaColumns.DISPLAY_NAME,MediaStore.MediaColumns.SIZE),"${MediaStore.MediaColumns.OWNER_PACKAGE_NAME}=? AND ${MediaStore.MediaColumns.IS_PENDING}=0",arrayOf(model.context.packageName),"${MediaStore.MediaColumns.DATE_ADDED} DESC")?.use{c->while(c.moveToNext()){
                val uri=ContentUris.withAppendedId(MediaStore.Downloads.EXTERNAL_CONTENT_URI,c.getLong(0)).toString()
                if(uri !in protect)result[uri]=StorageItem(uri,c.getString(1),c.getLong(2).coerceAtLeast(0))
            }}
        }.onFailure{warning="Some downloads could not be listed. Check folder access."}
        model.history.readableDatabase.rawQuery("SELECT savedUri,name,size FROM files WHERE savedUri IS NOT NULL AND outgoing=0",null).use{c->while(c.moveToNext()){
            val uri=c.getString(0)
            if(uri !in protect && uri !in result)result[uri]=StorageItem(uri,c.getString(1),c.getLong(2).coerceAtLeast(0))
        }}
        val keep=protect+linked(model);var kept=0L
        val root=File(model.context.filesDir,"outbox")
        root.takeIf{it.exists()}?.walkTopDown()?.filter{it.isFile}?.forEach{file->
            val uri=androidx.core.content.FileProvider.getUriForFile(model.context,model.context.packageName+".files",file).toString()
            if(uri in keep || System.currentTimeMillis()-file.lastModified()<3_600_000)kept+=file.length()
            else result[uri]=StorageItem(uri,file.name,file.length(),true,file)
        }
        return StorageSnapshot(result.values.toList(),android.os.StatFs(model.context.filesDir.absolutePath).availableBytes,kept,warning)
    }
    fun remove(model:Model,items:List<StorageItem>):Pair<Int,Int> {
        var removed=0;var failed=0
        items.distinctBy{it.uri}.forEach{item->runCatching{
            require(item.uri !in protected(model)){"This file is in use"}
            if(item.temporary){
                require(item.uri !in linked(model));val file=requireNotNull(item.local).canonicalFile
                val root=File(model.context.filesDir,"outbox").canonicalFile
                require(file.toPath().startsWith(root.toPath()) && file!=root && System.currentTimeMillis()-file.lastModified()>=3_600_000)
                require(!file.exists() || file.delete())
            }else{
                // Re-list before deletion: only app-owned downloads or recorded destination links qualify.
                require(scan(model).items.any{it.uri==item.uri && !it.temporary})
                val uri=Uri.parse(item.uri);val resolver=model.context.contentResolver
                val deleted=try {if(DocumentsContract.isDocumentUri(model.context,uri)) DocumentsContract.deleteDocument(resolver,uri) else resolver.delete(uri,null,null)>0} catch(_:java.io.FileNotFoundException){true}
                require(deleted){"File unavailable or folder access revoked"}
                val db=model.history.writableDatabase
                db.beginTransaction();try{
                    db.execSQL("UPDATE transfer_extra SET phase='REMOVED',failure=NULL,detail=NULL WHERE id IN (SELECT id FROM files WHERE savedUri=? OR sourceUri=?)",arrayOf(item.uri,item.uri))
                    db.update("files",ContentValues().apply{putNull("savedUri");putNull("sourceUri");put("status",TransferPhase.REMOVED.label)},"savedUri=? OR sourceUri=?",arrayOf(item.uri,item.uri));db.setTransactionSuccessful()
                }finally{db.endTransaction()}
                model.update{state->state.copy(transfers=state.transfers.map{if(it.savedUri==item.uri || it.sourceUri==item.uri)it.copy(phase=TransferPhase.REMOVED,savedUri=null,sourceUri=null)else it},revision=state.revision+1)}
            }
        }.onSuccess{removed++}.onFailure{failed++}}
        model.changed();return removed to failed
    }
}

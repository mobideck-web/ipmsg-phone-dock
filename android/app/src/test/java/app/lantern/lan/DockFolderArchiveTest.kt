package app.lantern.lan

import org.junit.Assert.*
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.File
import java.io.InputStream
import java.nio.file.Files
import java.util.zip.ZipFile

class DockFolderArchiveTest {
    private class Source:DockFolderArchive.Source {
        val nodes=linkedMapOf<String,DockFolderArchive.Node>()
        val children=mutableMapOf<String,MutableList<String>>()
        val bodies=mutableMapOf<String,ByteArray>()
        var opened:(String)->Unit={}
        fun add(key:String,name:String,directory:Boolean,parent:String?=null,bytes:ByteArray=ByteArray(0)):DockFolderArchive.Node {
            val node=DockFolderArchive.Node(key,name,directory,if(directory) 0 else bytes.size.toLong(),0,if(directory) "directory" else "application/octet-stream")
            nodes[key]=node;if(parent!=null) children.getOrPut(parent){mutableListOf()}.add(key);if(!directory) bodies[key]=bytes
            return node
        }
        override fun metadata(key:String)=nodes[key] ?: error("Revoked source")
        override fun children(key:String)=children[key].orEmpty().map{metadata(it)}
        override fun open(node:DockFolderArchive.Node):InputStream {opened(node.key);return ByteArrayInputStream(bodies[node.key] ?: error("Missing source"))}
    }
    private fun fixture(block:(Source,DockFolderArchive.Node,File)->Unit) {
        val dir=Files.createTempDirectory("ipmsg-folder-").toFile()
        try {val source=Source();val root=source.add("root","Photos",true);block(source,root,File(dir,"snapshot"))}
        finally {dir.listFiles()?.forEach{it.delete()};dir.delete()}
    }
    private fun create(source:Source,root:DockFolderArchive.Node,file:File,ensure:()->Unit={},free:()->Long={1024L*1024*1024})=
        DockFolderArchive.create(root,file,source,ensure,free)
    private fun fails(message:String?=null,block:()->Unit):Exception {
        try {block();fail("Expected rejection")}
        catch(error:Exception) {if(message!=null) assertTrue(error.message.orEmpty(),error.message.orEmpty().contains(message));return error}
        throw AssertionError()
    }
    @Test fun emptyFolderProducesAnEmptyStandardArchive()=fixture {s,root,file->
        val result=create(s,root,file);assertEquals("Photos.zip",result.name);assertEquals(file.length(),result.bytes)
        ZipFile(file).use{assertEquals(0,it.size())}
    }
    @Test fun nestedUnicodeFilesAndEmptyDirectoriesRemainRelativeAndExact()=fixture {s,root,file->
        s.add("d","旅行",true,"root");s.add("e","Empty",true,"root")
        val data=ByteArray(400_123){(it%251).toByte()};s.add("f","été 📷.jpg",false,"d",data);s.add("z","zero.txt",false,"root")
        val result=create(s,root,file);assertEquals(2,result.files)
        ZipFile(file).use {zip->
            assertEquals(setOf("旅行/","Empty/","旅行/été 📷.jpg","zero.txt"),zip.entries().asSequence().map{it.name}.toSet())
            assertArrayEquals(data,zip.getInputStream(zip.getEntry("旅行/été 📷.jpg")).readBytes())
            assertEquals(0L,zip.getEntry("zero.txt").size)
        }
    }
    @Test fun unsafeWindowsComponentsAreRejected() {
        for(name in listOf("..",".","../x","a/b","a\\b","a:b","a?b","CON","NUL.txt","COM1.log","LPT³","CONIN\$","CONOUT\$","end.","end ","bad\u0000","bad\u007f","bad\u009f","x".repeat(241),"\uD800"))
            assertFalse(name,DockFolderArchive.component(name))
        assertTrue(DockFolderArchive.component("notes.v2.txt"));assertTrue(DockFolderArchive.component("🐈"))
    }
    @Test fun pathTraversalAndReservedChildrenNeverPublishAFile()=fixture {s,root,file->
        s.add("bad","../outside",false,"root");fails{create(s,root,file)};assertFalse(file.exists())
    }
    @Test fun caseConflictingFilesAndDirectoriesAreRejected()=fixture {s,root,file->
        s.add("a","Document",true,"root");s.add("b","document",false,"root");fails("case-conflicting"){create(s,root,file)};assertFalse(file.exists())
    }
    @Test fun aliasesAndCyclesCannotEscapeOrRepeatTheTree()=fixture {s,root,file->
        s.children["root"]=mutableListOf("root");fails("cycle"){create(s,root,file)};assertFalse(file.exists())
    }
    @Test fun deepAndOversizedTreesFailBeforePublication()=fixture {s,root,file->
        var parent="root";repeat(33){index->val key="d$index";s.add(key,"d$index",true,parent);parent=key}
        fails("deeply"){create(s,root,file)};assertFalse(file.exists())
    }
    @Test fun countLimitIsAppliedBeforeCopyingBodies()=fixture {s,root,file->
        repeat(10_001){s.add("n$it","n$it",false,"root")};fails("10000"){create(s,root,file)};assertFalse(file.exists())
    }
    @Test fun knownByteLimitAndVirtualDocumentsAreRejected()=fixture {s,root,file->
        val node=s.add("f","huge.bin",false,"root");s.nodes["f"]=node.copy(size=DockFolderArchive.MAX_BYTES+1)
        fails("32 GB"){create(s,root,file)};assertFalse(file.exists())
        s.nodes["f"]=node.copy(virtual=true);fails("virtual"){create(s,root,file)};assertFalse(file.exists())
    }
    @Test fun lowStorageFailsWithoutLeavingASnapshot()=fixture {s,root,file->
        fails("Free phone storage"){create(s,root,file,free={DockFolderArchive.RESERVE_BYTES})};assertFalse(file.exists())
    }
    @Test fun insufficientStorageDuringWritingRemovesPartialArchive()=fixture {s,root,file->
        val node=s.add("f","large.bin",false,"root",ByteArray(1024));s.nodes["f"]=node.copy(size=-1)
        fails("storage"){create(s,root,file,free={DockFolderArchive.RESERVE_BYTES+500})};assertFalse(file.exists())
    }
    @Test fun knownPayloadAndZipOverheadAreCheckedBeforeOpeningAnyFile()=fixture {s,root,file->
        s.add("f","large.bin",false,"root",ByteArray(8192));var opened=0;s.opened={opened++}
        fails("storage"){create(s,root,file,free={DockFolderArchive.RESERVE_BYTES+8192})}
        assertEquals(0,opened);assertFalse(file.exists())
    }
    @Test fun inaccurateProviderSizesAreRejectedAndPartialDeleted()=fixture {s,root,file->
        val node=s.add("f","changed.txt",false,"root",byteArrayOf(1,2,3));s.nodes["f"]=node.copy(size=2)
        fails("changed"){create(s,root,file)};assertFalse(file.exists())
    }
    @Test fun changedMetadataAndNewChildrenInvalidateTheSnapshot()=fixture {s,root,file->
        val node=s.add("f","one.txt",false,"root",byteArrayOf(1))
        s.opened={s.nodes["f"]=node.copy(modified=99)};fails("changed"){create(s,root,file)};assertFalse(file.exists())
        s.nodes["f"]=node;s.opened={s.add("new","new.txt",false,"root")}
        fails("changed"){create(s,root,file)};assertFalse(file.exists())
    }
    @Test fun ownerRevocationClosesTheSnapshotWithoutPublishing()=fixture {s,root,file->
        s.add("f","one.txt",false,"root",ByteArray(400_000));var revoked=false
        s.opened={revoked=true};fails("revoked"){create(s,root,file,ensure={check(!revoked){"Owner revoked access"}})};assertFalse(file.exists())
    }
    @Test fun deadlineIsBoundedAndExistingPrivateFileIsNeverOverwritten()=fixture {s,root,file->
        var now=0L
        fails("timed out"){DockFolderArchive.create(root,file,s,{}, {1024*1024*1024L},{now.also{now+=600_001}})}
        assertFalse(file.exists());file.writeText("owned")
        fails{create(s,root,file)};assertEquals("owned",file.readText())
    }
}

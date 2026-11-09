package app.lantern.lan

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.unit.dp

@Composable fun IPMsgTheme(dark:Boolean,content:@Composable ()->Unit){
    val colors=if(dark)darkColorScheme(primary=Color(0xff90d6bd),onPrimary=Color(0xff12382c),primaryContainer=Color(0xff234e3e),onPrimaryContainer=Color(0xffcef3e1),secondary=Color(0xffb7c9c0),secondaryContainer=Color(0xff2a3e34),onSecondaryContainer=Color(0xffcbebda),onBackground=Color(0xffe4ece6),background=Color(0xff101916),surface=Color(0xff131e19),surfaceVariant=Color(0xff2a3831),surfaceContainer=Color(0xff1b2922),surfaceContainerLow=Color(0xff18241e),surfaceContainerHigh=Color(0xff25342c),surfaceContainerHighest=Color(0xff2c3c33),onSurface=Color(0xffe4ece6),onSurfaceVariant=Color(0xffb4c3b8),outline=Color(0xff7a8d80),outlineVariant=Color(0xff34483b))
    else lightColorScheme(primary=Color(0xff22664e),onPrimary=Color.White,primaryContainer=Color(0xffdef0e5),onPrimaryContainer=Color(0xff163c2a),secondary=Color(0xff526a5d),secondaryContainer=Color(0xffe7eee8),onSecondaryContainer=Color(0xff253b2e),onBackground=Color(0xff202b24),background=Color(0xfff7f8f4),surface=Color(0xfffdfefa),surfaceVariant=Color(0xffe9eee7),surfaceContainer=Color(0xffeef2eb),surfaceContainerLow=Color(0xfff0f4ed),surfaceContainerHigh=Color(0xffe6ece3),surfaceContainerHighest=Color(0xffe0e7dd),onSurface=Color(0xff202b24),onSurfaceVariant=Color(0xff58665b),outline=Color(0xff7b8b7d),outlineVariant=Color(0xffd5ded2))
    MaterialTheme(colorScheme=colors,shapes=Shapes(small=RoundedCornerShape(12.dp),medium=RoundedCornerShape(18.dp),large=RoundedCornerShape(24.dp)),content=content)
}
@Composable fun NavigationSymbol(tab:String){
    val color=LocalContentColor.current
    Canvas(Modifier.size(24.dp)){
        val scale=size.width/24f;val stroke=1.7f*scale
        fun line(x:Float,y:Float,x2:Float,y2:Float)=drawLine(color,Offset(x*scale,y*scale),Offset(x2*scale,y2*scale),stroke,StrokeCap.Round)
        when(tab){
            "Nearby"->{drawCircle(color,8.5f*scale,style=Stroke(stroke));drawCircle(color,2.5f*scale)}
            "Chats"->{drawRoundRect(color,Offset(3f*scale,3f*scale),Size(18f*scale,15f*scale),CornerRadius(4f*scale),style=Stroke(stroke));line(7f,18f,7f,21f);line(7f,21f,11f,18f);line(7f,8f,17f,8f);line(7f,12f,14f,12f)}
            "Transfers"->{line(12f,3f,12f,15f);line(7f,10f,12f,15f);line(17f,10f,12f,15f);line(4f,16f,4f,21f);line(4f,21f,20f,21f);line(20f,21f,20f,16f)}
            else->{drawCircle(color,7f*scale,style=Stroke(stroke));drawCircle(color,2.5f*scale,style=Stroke(stroke));line(12f,1f,12f,5f);line(12f,19f,12f,23f);line(1f,12f,5f,12f);line(19f,12f,23f,12f)}
        }
    }
}

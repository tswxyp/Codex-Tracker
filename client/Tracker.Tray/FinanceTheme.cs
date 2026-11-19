using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace CodexTracker.Tray;

internal sealed class FinanceTheme(bool dark,bool redGain)
{
    internal bool Dark=>dark;
    internal bool RedGain {get;set;}=redGain;
    internal Color Paper=>dark?Color.FromArgb(31,36,34):Color.White;
    internal Color Panel=>dark?Color.FromArgb(40,49,45):Color.FromArgb(242,247,244);
    internal Color Ink=>dark?Color.FromArgb(236,242,238):Color.FromArgb(30,47,41);
    internal Color Muted=>dark?Color.FromArgb(179,195,185):Color.FromArgb(83,103,92);
    internal Color Line=>dark?Color.FromArgb(65,80,71):Color.FromArgb(212,224,216);
    internal Color Accent=>dark?Color.FromArgb(137,218,184):Color.FromArgb(27,99,75);
    internal Color Profit(decimal? value){
        if(!value.HasValue || value==0)return Ink;
        bool red=(value>0)==RedGain;
        return red?(dark?Color.FromArgb(255,153,158):Color.FromArgb(173,58,70)):
            (dark?Color.FromArgb(119,217,171):Color.FromArgb(26,116,82));
    }
    internal static bool SystemDark(){
        using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int flag && flag==0;
    }
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(nint hwnd,int attribute,ref int value,int count);

    internal void Apply(Form form){
        void PaintControls(Control control){
            control.ForeColor=Ink;
            control.BackColor=control is Button or TextBoxBase or ComboBox or NumericUpDown?Panel:Paper;
            if(control is Button button){
                button.FlatStyle=FlatStyle.Flat;button.FlatAppearance.BorderColor=Line;
            }
            if(control is ComboBox combo)combo.FlatStyle=FlatStyle.Flat;
            foreach(Control child in control.Controls)PaintControls(child);
        }
        PaintControls(form);
        void Caption(){int value=dark?1:0;DwmSetWindowAttribute(form.Handle,20,ref value,sizeof(int));}
        if(form.IsHandleCreated)Caption();else form.HandleCreated+=(_,_)=>Caption();
    }
}

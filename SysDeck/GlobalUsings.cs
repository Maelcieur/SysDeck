// Alias globaux pour lever les ambiguïtés entre WPF (System.Windows.*) et
// WinForms (System.Windows.Forms.* / System.Drawing.*), activé pour l'icône
// système et le sélecteur de couleur natif. Sauf mention contraire, le projet
// utilise les types WPF.
global using Application = System.Windows.Application;
global using MessageBox = System.Windows.MessageBox;
global using Clipboard = System.Windows.Clipboard;
global using UserControl = System.Windows.Controls.UserControl;
global using Button = System.Windows.Controls.Button;
global using CheckBox = System.Windows.Controls.CheckBox;
global using RadioButton = System.Windows.Controls.RadioButton;
global using Color = System.Windows.Media.Color;
global using ColorConverter = System.Windows.Media.ColorConverter;
global using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
global using DragEventArgs = System.Windows.DragEventArgs;
global using DataFormats = System.Windows.DataFormats;
global using DragDropEffects = System.Windows.DragDropEffects;
global using Brush = System.Windows.Media.Brush;
global using Pen = System.Windows.Media.Pen;
global using FontFamily = System.Windows.Media.FontFamily;
global using Brushes = System.Windows.Media.Brushes;
global using MouseEventArgs = System.Windows.Input.MouseEventArgs;
global using Point = System.Windows.Point;
global using Orientation = System.Windows.Controls.Orientation;
global using Panel = System.Windows.Controls.Panel;
global using Size = System.Windows.Size;

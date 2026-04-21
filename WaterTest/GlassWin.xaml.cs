using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WaterTest.UI;

namespace WaterTest
{
    /// <summary>
    /// Interaction logic for GlassWin.xaml
    /// </summary>
    public partial class GlassWin : Window
    {
        public GlassMaterial GlassRef = null;
        public GlassWin()
        {
            InitializeComponent();

            GlassRef = new GlassMaterial(Root);
            GlassRef.Win = this;

            DateTime lastTime = DateTime.UtcNow;
            double Time = 0;
            double Step = 0.2;  

            GlassRef.OnFrameCaptured = (Bitmap) =>
            {
                Time += Step;
                return GlassRefraction.Refraction(Bitmap, 5, 1, Time);
            };
        }

        public bool IsLeftMouseDown = false;

        private void WinHead_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                IsLeftMouseDown = true;
            }

            if (IsLeftMouseDown)
            {
                try
                {
                    this.Dispatcher.Invoke(new Action(() =>
                    {
                        this.DragMove();
                    }));

                    IsLeftMouseDown = false;
                }
                catch { }
            }
        }


        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
           
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            GlassRef.Dispose();
            this.Hide();
            Environment.Exit(0);
        }
    }
}

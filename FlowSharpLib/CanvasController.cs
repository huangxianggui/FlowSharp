﻿/* The MIT License (MIT)
* 
* Copyright (c) 2016 Marc Clifton
* 
* Permission is hereby granted, free of charge, to any person obtaining a copy
* of this software and associated documentation files (the "Software"), to deal
* in the Software without restriction, including without limitation the rights
* to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
* copies of the Software, and to permit persons to whom the Software is
* furnished to do so, subject to the following conditions:
* 
* The above copyright notice and this permission notice shall be included in all
* copies or substantial portions of the Software.
* 
* THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
* IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
* FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
* AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
* LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
* OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
* SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace FlowSharpLib
{
	public class ElementEventArgs : EventArgs
	{
		public GraphicElement Element { get; set; }
	}

	public class SnapInfo
	{
		public GraphicElement NearElement { get; set; }
		public ConnectionPoint LineConnectionPoint { get; set; }
	}

	public class CanvasController : BaseController
	{
		protected Point mousePosition;
		protected List<SnapInfo> currentlyNear = new List<SnapInfo>();
		
		public CanvasController(Canvas canvas, List<GraphicElement> elements) : base(canvas, elements)
		{
			canvas.Controller = this;
			canvas.PaintComplete = CanvasPaintComplete;
            canvas.MouseDown += OnMouseDown;
            canvas.MouseUp += OnMouseUp;
			canvas.MouseMove += OnMouseMove;
		}

        protected void OnMouseDown(object sender, MouseEventArgs args)
        {
            if (args.Button == MouseButtons.Left)
            {
                leftMouseDown = true;
                DeselectCurrentSelectedElement();
                SelectElement(args.Location);
                selectedAnchor = selectedElement?.GetAnchors().FirstOrDefault(a => a.Near(mousePosition));
                ElementSelected.Fire(this, new ElementEventArgs() { Element = selectedElement });
                dragging = selectedElement != null;
                mousePosition = args.Location;
            }
        }

        protected void OnMouseUp(object sender, MouseEventArgs args)
        {
            if (args.Button == MouseButtons.Left)
            {
                selectedAnchor = null;
                leftMouseDown = false;
                dragging = false;
                ShowConnectionPoints(currentlyNear.Select(e => e.NearElement), false);
                currentlyNear.Clear();
            }
        }

        protected void OnMouseMove(object sender, MouseEventArgs args)
		{
			Point delta = args.Location.Delta(mousePosition);

			// Weird - on click, the mouse move event appears to fire as well, so we need to check
			// for no movement in order to prevent detaching connectors!
			if (delta == Point.Empty) return;

			mousePosition = args.Location;

			if (dragging)
			{
				if (selectedAnchor != null)
				{
					// Snap the anchor?
					bool connectorAttached = selectedElement.SnapCheck(selectedAnchor, delta);

					if (!connectorAttached)
					{
						selectedElement.DisconnectShapeFromConnector(selectedAnchor.Type);
						selectedElement.RemoveConnection(selectedAnchor.Type);
					}
				}
				else
				{
					DragSelectedElement(delta);
				}

			}
			else if (leftMouseDown)
			{
                // Pick up every object on the canvas and move it.
                // This does not "move" the grid.
                MoveAllElements(delta);

				// Conversely, we redraw the grid and invalidate, which forces all the elements to redraw.
				//canvas.Drag(delta);
				//elements.ForEach(el => el.Move(delta));
				//canvas.Invalidate();
			}
			else
			{
				GraphicElement el = elements.FirstOrDefault(e => e.IsSelectable(mousePosition));

				// Remove anchors from current object being moused over and show, if an element selected on new object.
				if (el != showingAnchorsElement)
				{
					if (showingAnchorsElement != null)
					{
						showingAnchorsElement.ShowAnchors = false;
						Redraw(showingAnchorsElement);
						showingAnchorsElement = null;
					}

					if (el != null)
					{
						el.ShowAnchors = true;
						Redraw(el);
						showingAnchorsElement = el;
					}
				}
			}
		}

		public void DragSelectedElement(Point delta)
		{
			bool connectorAttached = selectedElement.SnapCheck(GripType.Start, ref delta) || selectedElement.SnapCheck(GripType.End, ref delta);
			selectedElement.Connections.ForEach(c => c.ToElement.MoveElementOrAnchor(c.ToConnectionPoint.Type, delta));
			MoveElement(selectedElement, delta);
			UpdateSelectedElement.Fire(this, new ElementEventArgs() { Element = SelectedElement });

			if (!connectorAttached)
			{
				DetachFromAllShapes(selectedElement);
			}
		}

		protected void DetachFromAllShapes(GraphicElement el)
		{
			el.DisconnectShapeFromConnector(GripType.Start);
			el.DisconnectShapeFromConnector(GripType.End);
			el.RemoveConnection(GripType.Start);
			el.RemoveConnection(GripType.End);
		}

		public override bool Snap(GripType type, ref Point delta)
		{
			bool snapped = false;

			// Look for connection points on nearby elements.
			// If a connection point is nearby, and the delta is moving toward that connection point, then snap to that connection point.

			// So, it seems odd that we're using the connection points of the line, rather than the anchors.
			// However, this is actually simpler, and a line's connection points should at least include the endpoint anchors.
			IEnumerable<ConnectionPoint> connectionPoints = selectedElement.GetConnectionPoints().Where(p => type == GripType.None || p.Type == type);
			List<SnapInfo> nearElements = GetNearbyElements(connectionPoints);
			ShowConnectionPoints(nearElements.Select(e=>e.NearElement), true);
			ShowConnectionPoints(currentlyNear.Where(e => !nearElements.Any(e2 => e.NearElement == e2.NearElement)).Select(e=>e.NearElement), false);
			currentlyNear = nearElements;
			
			foreach (SnapInfo si in nearElements)
			{
				ConnectionPoint nearConnectionPoint = si.NearElement.GetConnectionPoints().FirstOrDefault(cp => cp.Point.IsNear(si.LineConnectionPoint.Point, SNAP_CONNECTION_POINT_RANGE));

				if (nearConnectionPoint != null)
				{
					Point sourceConnectionPoint = si.LineConnectionPoint.Point;
					int neardx = nearConnectionPoint.Point.X - sourceConnectionPoint.X;     // calculate to match possible delta sign
					int neardy = nearConnectionPoint.Point.Y - sourceConnectionPoint.Y;
					int neardxsign = neardx.Sign();
					int neardysign = neardy.Sign();
					int deltaxsign = delta.X.Sign();
					int deltaysign = delta.Y.Sign();

                    // Are we attached already or moving toward the shape's connection point?
					if ((neardxsign == 0 || deltaxsign == 0 || neardxsign == deltaxsign) &&
							(neardysign == 0 || deltaysign == 0 || neardysign == deltaysign))
					{
                        // If attached, are we moving away from the connection point to detach it?
                        if (neardxsign == 0 && neardxsign == 0 && (delta.X.Abs() >= SNAP_DETACH_VELOCITY || delta.Y.Abs() >= SNAP_DETACH_VELOCITY))
						{
							selectedElement.DisconnectShapeFromConnector(type);
							selectedElement.RemoveConnection(type);
						}
						else
						{
                            // Not already connected?
							// if (!si.NearElement.Connections.Any(c => c.ToElement == selectedElement))
                            if (neardxsign != 0 || neardysign != 0)
							{
								si.NearElement.Connections.Add(new Connection() { ToElement = selectedElement, ToConnectionPoint = si.LineConnectionPoint, ElementConnectionPoint = nearConnectionPoint });
								selectedElement.SetConnection(si.LineConnectionPoint.Type, si.NearElement);
							}

							delta = new Point(neardx, neardy);
							snapped = true;
							break;
						}
					}
				}
			}

			return snapped;
		}

		protected virtual List<SnapInfo> GetNearbyElements(IEnumerable<ConnectionPoint> connectionPoints)
		{
			List<SnapInfo> nearElements = new List<SnapInfo>();

			elements.Where(e=>e != selectedElement && e.OnScreen() && !e.IsConnector).ForEach(e =>
			{
				Rectangle checkRange = e.DisplayRectangle.Grow(SNAP_ELEMENT_RANGE);

				connectionPoints.ForEach(cp =>
				{
					if (checkRange.Contains(cp.Point))
					{
						nearElements.Add(new SnapInfo() { NearElement = e, LineConnectionPoint = cp });
					}
				});
			});

			return nearElements;
		}

		protected virtual void ShowConnectionPoints(IEnumerable<GraphicElement> elements, bool state)
		{ 
			elements.ForEach(e =>
			{
				e.ShowConnectionPoints = state;
				Redraw(e, CONNECTION_POINT_SIZE, CONNECTION_POINT_SIZE);
			});
		}

		public void DeselectCurrentSelectedElement()
		{
			if (selectedElement != null)
			{
				var els = EraseTopToBottom(selectedElement);
				selectedElement.Selected = false;
				DrawBottomToTop(els);
				UpdateScreen(els);
				selectedElement = null;
			}
		}

		protected bool SelectElement(Point p)
		{
			GraphicElement el = elements.FirstOrDefault(e => e.IsSelectable(p));

			if (el != null)
			{
				SelectElement(el);
			}

			return el != null;
		}

		public void SelectElement(GraphicElement el)
		{
            DeselectCurrentSelectedElement();
            var els = EraseTopToBottom(el);
			el.Selected = true;
			DrawBottomToTop(els);
			UpdateScreen(els);
			selectedElement = el;
            ElementSelected.Fire(this, new ElementEventArgs() { Element = el });
        }
	}
}